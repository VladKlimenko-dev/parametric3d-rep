using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;

namespace Parametric3D.Kernel.Geometry;

/// <summary>
/// KRN-07 (07.8), KRN-04 (04.9), шаг N1b: ПЕРЕСЕЧЕНИЕ ГРАНИ NURBS С ПЛОСКОСТЬЮ ИЛИ КВАДРИКОЙ — нулевое множество функции
/// G(u, v) = F(S(u, v)) в прямоугольнике параметров грани, где F — неявная форма другой поверхности (расстояние со знаком,
/// константа Липшица 1). Точки кривой лежат на сплайне точно (вычисляются по S), F уточняется до 1e-12.
///   затравки — смены знака G на сетке 64×64 прямоугольника (с уточнением);
///   шаг — вдоль касательной к G = 0 в параметрах, нормированной на 1 мм пути, поправка — Ньютон по градиенту G;
///   выход за прямоугольник — точка на его стороне (деление пополам);
///   ПОЛНОТА — куски Безье (строгий габарит по выпуклой оболочке): кусок исключён, если |F(центр габарита)| больше половины
///   его диагонали; иначе — пояснён найденной кривой или делится; малый непояснённый кусок — новая затравка (Ньютон из
///   центра) или отказ (Uncertified).
/// </summary>
public static class NurbsSsi
{
    public static Ssi.Result Intersect(NurbsSurface S, double ua, double ub, double va, double vb, Ssi.Shape other, double scale)
    {
        var r = new Ssi.Result();
        double G(Vector2d q) => Ssi.Implicit(other, S.PointAt(q)).F;
        (double Gv, Vector2d Grad, Vector3d Su, Vector3d Sv) GD(Vector2d q)
        {
            const double h = 1e-7;
            double u = q.X, v = q.Y;
            double u0 = Math.Max(0, u - h), u1 = Math.Min(1, u + h), v0 = Math.Max(0, v - h), v1 = Math.Min(1, v + h);
            var X = S.PointAt(q);
            var su = (S.PointAt(new Vector2d(u1, v)) - S.PointAt(new Vector2d(u0, v))) / (u1 - u0);
            var sv = (S.PointAt(new Vector2d(u, v1)) - S.PointAt(new Vector2d(u, v0))) / (v1 - v0);
            var (f, g) = Ssi.Implicit(other, X);
            return (f, new Vector2d(Vector3d.Dot(g, su), Vector3d.Dot(g, sv)), su, sv);
        }
        bool InRect(Vector2d q) => q.X >= ua - 1e-12 && q.X <= ub + 1e-12 && q.Y >= va - 1e-12 && q.Y <= vb + 1e-12;
        Vector2d? Correct(Vector2d q)
        {
            for (int it = 0; it < 30; it++)
            {
                var (gv, gr, _, _) = GD(q);
                if (Math.Abs(gv) < 1e-12) return q;
                double n2 = gr.LengthSquared; if (n2 < 1e-30) return null;
                var nq = q - gr * (gv / n2);
                nq = new Vector2d(Math.Clamp(nq.X, 0, 1), Math.Clamp(nq.Y, 0, 1));
                if ((nq - q).Length < 1e-16) return Math.Abs(G(nq)) < 1e-9 ? nq : null;
                q = nq;
            }
            return Math.Abs(G(q)) < 1e-9 ? q : null;
        }
        // затравки
        int N = 64; var seeds = new List<Vector2d>();
        var gridG = new double[N + 1, N + 1];
        Vector2d UV(int i, int j) => new(ua + (ub - ua) * i / N, va + (vb - va) * j / N);
        for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++) gridG[i, j] = G(UV(i, j));
        for (int i = 0; i <= N; i++)
            for (int j = 0; j <= N; j++)
                foreach (var (di, dj) in new[] { (1, 0), (0, 1) })
                {
                    if (i + di > N || j + dj > N) continue;
                    double f0 = gridG[i, j], f1 = gridG[i + di, j + dj];
                    if (!(f0 == 0 || f0 * f1 < 0)) continue;
                    var a = UV(i, j); var b = UV(i + di, j + dj); double lo = 0, hi = 1;
                    for (int it = 0; it < 60; it++) { double m = (lo + hi) / 2; if (Math.Sign(G(a + (b - a) * m)) == Math.Sign(f0)) lo = m; else hi = m; }
                    if (Correct(a + (b - a) * lo) is { } q && InRect(q)) seeds.Add(q);
                }
        double h = Math.Max(scale / 400, 1e-4);              // шаг, мм
        var traced = new List<List<Vector2d>>();
        var tracedPts = new List<Vector3d>();
        bool Near(Vector2d q) { var x = S.PointAt(q); return tracedPts.Any(p => (p - x).LengthSquared < 4 * h * h); }
        Vector2d ToRect(Vector2d inside, Vector2d outside)
        {
            // точка кривой на стороне прямоугольника: деление пополам по отрезку с поправкой на G = 0
            Vector2d a = inside, b = outside;
            for (int it = 0; it < 60; it++)
            {
                var m = (a + b) / 2; var c = Correct(m) ?? m;
                if (InRect(c)) a = c; else b = c;
            }
            // на сторону — точно (параметр, ближайший к стороне, равен её значению), затем поправка вдоль стороны
            var q = a;
            double dua = Math.Abs(q.X - ua), dub = Math.Abs(q.X - ub), dva = Math.Abs(q.Y - va), dvb = Math.Abs(q.Y - vb);
            double mn = Math.Min(Math.Min(dua, dub), Math.Min(dva, dvb));
            bool fixU = mn == dua || mn == dub; double fixed_ = mn == dua ? ua : mn == dub ? ub : mn == dva ? va : vb;
            for (int it = 0; it < 40; it++)
            {
                var p = fixU ? new Vector2d(fixed_, q.Y) : new Vector2d(q.X, fixed_);
                var (gv, gr, _, _) = GD(p);
                double d = fixU ? gr.Y : gr.X; if (Math.Abs(d) < 1e-30) { q = p; break; }
                var np = fixU ? new Vector2d(fixed_, Math.Clamp(p.Y - gv / d, va, vb)) : new Vector2d(Math.Clamp(p.X - gv / d, ua, ub), fixed_);
                q = np; if (Math.Abs(gv) < 1e-13) break;
            }
            return q;
        }
        void TraceFrom(Vector2d s0)
        {
            if (Near(s0)) return;
            List<Vector2d> Walk(double dir, out bool closed)
            {
                closed = false;
                var pts = new List<Vector2d> { s0 }; var q = s0; var start3 = S.PointAt(s0);
                for (int step = 0; step < 50000; step++)
                {
                    var (_, gr, su, sv) = GD(q);
                    var t = new Vector2d(-gr.Y, gr.X) * dir;
                    var t3 = su * t.X + sv * t.Y; double sp = t3.Length;
                    if (sp < 1e-14) break;                                     // касание (градиент G ≈ 0)
                    var pred = q + t * (h / sp);
                    var nq = Correct(new Vector2d(Math.Clamp(pred.X, 0, 1), Math.Clamp(pred.Y, 0, 1)));
                    if (nq == null) { if (!InRect(pred) || pred.X < 0 || pred.X > 1 || pred.Y < 0 || pred.Y > 1) pts.Add(ToRect(q, pred)); break; }
                    if (!InRect(nq.Value) || pred.X < 0 || pred.X > 1 || pred.Y < 0 || pred.Y > 1) { pts.Add(ToRect(q, nq.Value)); break; }
                    q = nq.Value;
                    if (step > 4 && (S.PointAt(q) - start3).Length < 0.8 * h) { closed = true; break; }
                    pts.Add(q);
                }
                return pts;
            }
            var fwd = Walk(1, out bool cl);
            List<Vector2d> curve;
            if (cl) { curve = fwd; curve.Add(fwd[0]); }
            else { var back = Walk(-1, out _); back.Reverse(); back.RemoveAt(back.Count - 1); curve = back; curve.AddRange(fwd); }
            if (curve.Count < 2) return;
            traced.Add(curve); foreach (var q in curve) tracedPts.Add(S.PointAt(q));
        }
        foreach (var s0 in seeds) TraceFrom(s0);
        // полнота — по кускам Безье
        string? Certify(List<Vector2d>? extra)
        {
            double rhoMin = 1e-4 * Math.Max(1, scale);
            var stack = new Stack<BezierPatch>();
            foreach (var bp in NurbsTools.Beziers(S)) if (bp.U1 > ua && bp.U0 < ub && bp.V1 > va && bp.V0 < vb) stack.Push(bp);
            int visited = 0; string? fail = null;
            while (stack.Count > 0)
            {
                if (++visited > 200000) return "NURBS certificate: too many cells";
                var bp = stack.Pop();
                var (lo, hi) = bp.Box(); var c = (lo + hi) / 2; double rad = (hi - lo).Length / 2;
                double f = Math.Abs(Ssi.Implicit(other, c).F);
                if (f > rad * (1 + 1e-12) + 1e-12) continue;                     // исключён: поверхность другой формы не задевает габарит
                if (tracedPts.Any(p => (p - c).LengthSquared <= (rad + 2 * h) * (rad + 2 * h))) continue;   // пояснён найденной кривой
                if (rad > rhoMin)
                {
                    var (A, B) = bp.Split(bp.U1 - bp.U0 >= bp.V1 - bp.V0 ? true : false);
                    // от длинной стороны в мм: делить по тому направлению, где габарит длиннее (по параметру — как приближение)
                    stack.Push(A); stack.Push(B); continue;
                }
                var cuv = new Vector2d(Math.Clamp((bp.U0 + bp.U1) / 2, ua, ub), Math.Clamp((bp.V0 + bp.V1) / 2, va, vb));
                if (Correct(cuv) is { } q)
                {
                    if (!InRect(q)) continue;                                      // ветвь вне грани
                    if (Near(q)) continue;
                    if (extra == null) return $"NURBS certificate: an intersection point near {S.PointAt(q):0.######} is on no traced curve";
                    extra.Add(q); fail ??= "new seeds"; continue;
                }
                return $"NURBS certificate: unexplained near-contact cell at {c:0.######} (|F| {f:G3}, radius {rad:G3})";
            }
            return fail;
        }
        if (!CertOff)
            for (int round = 0; round < 3; round++)
            {
                var extra = new List<Vector2d>();
                var why = Certify(extra);
                if (why == null) break;
                int before = traced.Count;
                foreach (var q in extra) TraceFrom(q);
                if (traced.Count == before || round == 2) { if (Certify(null) is { } w2) r.Uncertified = w2; break; }
            }
        foreach (var c in traced)
            if (c.Count >= 2) r.Curves.Add(new NurbsImplicitCurve3(S, other, c));
        r.Kind = r.Curves.Count > 0 ? Ssi.Kind.Curves : Ssi.Kind.Empty;
        return r;
    }

    /// <summary>Мутация: P3D_NURBSCERT=0 — без удостоверения полноты (ветвь, пропущенная сеткой затравок, теряется).</summary>
    static readonly bool CertOff = Environment.GetEnvironmentVariable("P3D_NURBSCERT") == "0";
}

/// <summary>
/// Кривая пересечения грани NURBS и неявной поверхности: ломаная в параметрах сплайна (затравка), параметр — доля длины
/// пути в пространстве; точка — по S в интерполированных параметрах с поправкой на G = 0 вдоль градиента (лежит на сплайне
/// точно и на другой поверхности до 1e-12).
/// </summary>
public sealed class NurbsImplicitCurve3 : ICurve3
{
    public readonly NurbsSurface S; public readonly Ssi.Shape Other;
    readonly Vector2d[] _uv; readonly double[] _s;
    public IReadOnlyList<Vector2d> SeedUV => _uv;
    public NurbsImplicitCurve3(NurbsSurface s, Ssi.Shape other, IReadOnlyList<Vector2d> uv)
    {
        S = s; Other = other; _uv = uv.ToArray(); _s = new double[_uv.Length];
        for (int i = 1; i < _uv.Length; i++) _s[i] = _s[i - 1] + (S.PointAt(_uv[i]) - S.PointAt(_uv[i - 1])).Length;
    }
    public double T0 => 0; public double T1 => 1;
    /// <summary>Изломы параметризации (доли t точек затравки): квадратура — по кускам между ними.</summary>
    public IEnumerable<double> Breaks { get { double tot = _s[^1]; if (tot <= 0) yield break; for (int i = 1; i < _s.Length - 1; i++) yield return _s[i] / tot; } }
    public bool IsClosed => _uv.Length > 2 && (_uv[0] - _uv[^1]).Length < 1e-12;
    public double Length => _s[^1];

    public Vector2d UVAt(double t)
    {
        double tot = _s[^1]; if (tot <= 0) return _uv[0];
        double L = Math.Clamp(t, 0, 1) * tot;
        int lo = 0, hi = _s.Length - 1;
        while (hi - lo > 1) { int m = (lo + hi) / 2; if (_s[m] <= L) lo = m; else hi = m; }
        double seg = _s[hi] - _s[lo], a = seg > 0 ? (L - _s[lo]) / seg : 0;
        var q = _uv[lo] + (_uv[hi] - _uv[lo]) * a;
        if (a <= 0 || a >= 1) return q;                               // точки затравки — уже на кривой
        // поправка вдоль градиента G
        const double h = 1e-7;
        for (int it = 0; it < 8; it++)
        {
            var X = S.PointAt(q); var (f, g) = Ssi.Implicit(Other, X);
            if (Math.Abs(f) < 1e-13) break;
            double u0 = Math.Max(0, q.X - h), u1 = Math.Min(1, q.X + h), v0 = Math.Max(0, q.Y - h), v1 = Math.Min(1, q.Y + h);
            var su = (S.PointAt(new Vector2d(u1, q.Y)) - S.PointAt(new Vector2d(u0, q.Y))) / (u1 - u0);
            var sv = (S.PointAt(new Vector2d(q.X, v1)) - S.PointAt(new Vector2d(q.X, v0))) / (v1 - v0);
            var gr = new Vector2d(Vector3d.Dot(g, su), Vector3d.Dot(g, sv)); double n2 = gr.LengthSquared; if (n2 < 1e-30) break;
            q -= gr * (f / n2);
        }
        return q;
    }
    public Vector3d PointAt(double t) => S.PointAt(UVAt(t));
    public Vector3d TangentAt(double t)
    {
        double h = 1e-6; var a = PointAt(Math.Max(0, t - h)); var b = PointAt(Math.Min(1, t + h));
        var d = b - a; return d.LengthSquared < 1e-30 ? Vector3d.UnitX : d.Normalized();
    }
    public Bounds3 Bounds
    {
        get
        {
            var b = Bounds3.Empty; foreach (var q in _uv) b.Add(S.PointAt(q));
            // между точками затравки кривая отходит от хорды не больше шага — запас
            double step = 0; for (int i = 1; i < _s.Length; i++) step = Math.Max(step, _s[i] - _s[i - 1]);
            b.Add(b.Min - new Vector3d(step)); b.Add(b.Max + new Vector3d(step));
            return b;
        }
    }
    public ICurve3 Reversed() => new NurbsImplicitCurve3(S, Other, _uv.Reverse().ToList());
    public void Tessellate(List<Vector3d> dst, bool includeFirstPoint)
    {
        for (int i = includeFirstPoint ? 0 : 1; i < _uv.Length; i++) dst.Add(S.PointAt(_uv[i]));
    }
}

/// <summary>
/// KRN-04 (04.9), шаг N2: ПЕРЕСЕЧЕНИЕ ДВУХ ГРАНЕЙ NURBS. Неизвестные (u₁, v₁, u₂, v₂), уравнения S₁ = S₂ (три) и плоскость шага.
///   затравки — смены знака расстояния до другой поверхности (по нормали в проекции) на сетке 32×32 каждой грани;
///   шаг — вдоль n₁ × n₂, поправка Гаусса — Ньютона (4×4 с плоскостью шага); выход за прямоугольник грани — делением пополам
///   и точно на сторону (параметр стороны закреплён, три уравнения на три неизвестные);
///   ПОЛНОТА — пары кусков Безье: габариты врозь — пара исключена; иначе пара поясняется найденной кривой (точка ближе
///   радиуса куска + 2 шага) или делится; малая непояснённая пара — затравка (Ньютон из центров) или отказ.
/// </summary>
public static class NurbsNurbsSsi
{
    [ThreadStatic] public static Action<string>? Debug;
    public readonly record struct Rect(double U0, double U1, double V0, double V1)
    {
        public bool In(double u, double v) => u >= U0 - 1e-12 && u <= U1 + 1e-12 && v >= V0 - 1e-12 && v <= V1 + 1e-12;
    }

    static bool Solve(double[,] m, double[] b, int n)
    {
        for (int c = 0; c < n; c++)
        {
            int p = c; for (int r = c + 1; r < n; r++) if (Math.Abs(m[r, c]) > Math.Abs(m[p, c])) p = r;
            if (Math.Abs(m[p, c]) < 1e-300) return false;
            if (p != c) { for (int k = 0; k < n; k++) (m[c, k], m[p, k]) = (m[p, k], m[c, k]); (b[c], b[p]) = (b[p], b[c]); }
            for (int r = c + 1; r < n; r++) { double f = m[r, c] / m[c, c]; for (int k = c; k < n; k++) m[r, k] -= f * m[c, k]; b[r] -= f * b[c]; }
        }
        for (int r = n - 1; r >= 0; r--) { double s = b[r]; for (int k = r + 1; k < n; k++) s -= m[r, k] * b[k]; b[r] = s / m[r, r]; }
        return true;
    }

    /// <summary>Поправка Гаусса — Ньютона к S₁(q.xy) = S₂(q.zw); с плоскостью (x0, t) — 4×4, иначе — наименьшая норма шага.</summary>
    internal static Vector4d? Correct(NurbsSurface A, NurbsSurface B, Vector4d q, Vector3d? x0, Vector3d? t, int fixIdx = -1)
    {
        for (int it = 0; it < 40; it++)
        {
            A.Derivs(new Vector2d(q.X, q.Y), out var Pa, out var Au, out var Av);
            B.Derivs(new Vector2d(q.Z, q.W), out var Pb, out var Bu, out var Bv);
            var r = Pa - Pb; double pr = x0 is { } xx && t is { } tt ? Vector3d.Dot(tt, Pa - xx) : 0;
            if (r.Length < 1e-13 && Math.Abs(pr) < 1e-13) return q;
            var cols = new[] { Au, Av, -Bu, -Bv };
            double[] d;
            if (fixIdx >= 0)
            {
                // один параметр закреплён (сторона прямоугольника): 3×3
                var idx = Enumerable.Range(0, 4).Where(k => k != fixIdx).ToArray();
                var m = new double[3, 3]; var b = new[] { -r.X, -r.Y, -r.Z };
                for (int row = 0; row < 3; row++) for (int c = 0; c < 3; c++) m[row, c] = row == 0 ? cols[idx[c]].X : row == 1 ? cols[idx[c]].Y : cols[idx[c]].Z;
                if (!Solve(m, b, 3)) return null;
                d = new double[4]; for (int c = 0; c < 3; c++) d[idx[c]] = b[c];
            }
            else if (x0 != null && t is { } tv)
            {
                var m = new double[4, 4]; var b = new[] { -r.X, -r.Y, -r.Z, -pr };
                for (int c = 0; c < 4; c++) { m[0, c] = cols[c].X; m[1, c] = cols[c].Y; m[2, c] = cols[c].Z; }
                m[3, 0] = Vector3d.Dot(tv, Au); m[3, 1] = Vector3d.Dot(tv, Av); m[3, 2] = 0; m[3, 3] = 0;
                if (!Solve(m, b, 4)) return null;
                d = b;
            }
            else
            {
                // наименьшая норма: Δ = Jᵀ (J Jᵀ)⁻¹ (−r)
                var JJ = new double[3, 3];
                for (int a = 0; a < 3; a++) for (int c = 0; c < 3; c++) { double s2 = 0; for (int k = 0; k < 4; k++) s2 += Comp(cols[k], a) * Comp(cols[k], c); JJ[a, c] = s2; }
                var y = new[] { -r.X, -r.Y, -r.Z };
                if (!Solve(JJ, y, 3)) return null;
                d = new double[4]; for (int k = 0; k < 4; k++) d[k] = Comp(cols[k], 0) * y[0] + Comp(cols[k], 1) * y[1] + Comp(cols[k], 2) * y[2];
            }
            var nq = new Vector4d(Math.Clamp(q.X + d[0], 0, 1), Math.Clamp(q.Y + d[1], 0, 1), Math.Clamp(q.Z + d[2], 0, 1), Math.Clamp(q.W + d[3], 0, 1));
            if ((nq - q).Length < 1e-16) { q = nq; break; }
            q = nq;
        }
        A.Derivs(new Vector2d(q.X, q.Y), out var Pa2, out _, out _); B.Derivs(new Vector2d(q.Z, q.W), out var Pb2, out _, out _);
        return (Pa2 - Pb2).Length < 1e-10 ? q : null;
    }
    static double Comp(Vector3d v, int i) => i == 0 ? v.X : i == 1 ? v.Y : v.Z;

    public static Ssi.Result Intersect(NurbsSurface A, Rect ra, NurbsSurface B, Rect rb, double scale)
    {
        var res = new Ssi.Result();
        bool In(Vector4d q) => ra.In(q.X, q.Y) && rb.In(q.Z, q.W);
        double h = Math.Max(scale / 400, 1e-4);
        var seeds = new List<Vector4d>();
        // затравки: расстояние до другой поверхности на сетке каждой грани
        foreach (bool swap in new[] { false, true })
        {
            var P = swap ? B : A; var Q = swap ? A : B; var rp = swap ? rb : ra; var rq = swap ? ra : rb;
            int N = 32; var dv = new double[N + 1, N + 1]; var uvq = new Vector2d[N + 1, N + 1]; var ok = new bool[N + 1, N + 1];
            Vector2d hint = Q.ProjectToUV(P.PointAt(new Vector2d(rp.U0, rp.V0)));
            for (int i = 0; i <= N; i++)
                for (int j = 0; j <= N; j++)
                {
                    var uv = new Vector2d(rp.U0 + (rp.U1 - rp.U0) * i / N, rp.V0 + (rp.V1 - rp.V0) * j / N);
                    var x = P.PointAt(uv); var w = Q.ProjectToUV(x, hint); hint = w;
                    uvq[i, j] = w; ok[i, j] = w.X > rq.U0 + 1e-9 && w.X < rq.U1 - 1e-9 && w.Y > rq.V0 + 1e-9 && w.Y < rq.V1 - 1e-9;
                    dv[i, j] = Vector3d.Dot(x - Q.PointAt(w), Q.NormalAt(w));
                }
            for (int i = 0; i <= N; i++)
                for (int j = 0; j <= N; j++)
                    foreach (var (di, dj) in new[] { (1, 0), (0, 1) })
                    {
                        if (i + di > N || j + dj > N || !ok[i, j] || !ok[i + di, j + dj]) continue;
                        if (!(dv[i, j] * dv[i + di, j + dj] < 0 || dv[i, j] == 0)) continue;
                        var u0 = new Vector2d(rp.U0 + (rp.U1 - rp.U0) * i / N, rp.V0 + (rp.V1 - rp.V0) * j / N);
                        var u1 = new Vector2d(rp.U0 + (rp.U1 - rp.U0) * (i + di) / N, rp.V0 + (rp.V1 - rp.V0) * (j + dj) / N);
                        var m = (u0 + u1) / 2; var w = (uvq[i, j] + uvq[i + di, j + dj]) / 2;
                        var q0 = swap ? new Vector4d(w.X, w.Y, m.X, m.Y) : new Vector4d(m.X, m.Y, w.X, w.Y);
                        if (Correct(A, B, q0, null, null) is { } q && In(q)) seeds.Add(q);
                    }
        }
        var traced = new List<List<Vector4d>>(); var tracedPts = new List<Vector3d>();
        bool Near(Vector3d x, double rr) => tracedPts.Any(p => (p - x).LengthSquared <= rr * rr);
        Vector4d ToBoundary(Vector4d inside, Vector4d outside)
        {
            Vector4d a = inside, b = outside;
            for (int it = 0; it < 50; it++) { var mm = (a + b) / 2; var c = Correct(A, B, mm, null, null) ?? mm; if (In(c)) a = c; else b = c; }
            // какой параметр у стороны и её значение
            var cand = new (int Idx, double Val, double D)[]
            {
                (0, ra.U0, Math.Abs(a.X - ra.U0)), (0, ra.U1, Math.Abs(a.X - ra.U1)), (1, ra.V0, Math.Abs(a.Y - ra.V0)), (1, ra.V1, Math.Abs(a.Y - ra.V1)),
                (2, rb.U0, Math.Abs(a.Z - rb.U0)), (2, rb.U1, Math.Abs(a.Z - rb.U1)), (3, rb.V0, Math.Abs(a.W - rb.V0)), (3, rb.V1, Math.Abs(a.W - rb.V1)),
            };
            var best = cand.OrderBy(c => c.D).First();
            var q = best.Idx switch { 0 => new Vector4d(best.Val, a.Y, a.Z, a.W), 1 => new Vector4d(a.X, best.Val, a.Z, a.W), 2 => new Vector4d(a.X, a.Y, best.Val, a.W), _ => new Vector4d(a.X, a.Y, a.Z, best.Val) };
            return Correct(A, B, q, null, null, best.Idx) ?? a;
        }
        void TraceFrom(Vector4d s0)
        {
            if (Near(A.PointAt(new Vector2d(s0.X, s0.Y)), 2 * h)) return;
            List<Vector4d> Walk(double dir, out bool closed)
            {
                closed = false; var pts = new List<Vector4d> { s0 }; var q = s0; var start3 = A.PointAt(new Vector2d(s0.X, s0.Y));
                for (int step = 0; step < 50000; step++)
                {
                    A.Derivs(new Vector2d(q.X, q.Y), out var Pa, out var Au, out var Av);
                    B.Derivs(new Vector2d(q.Z, q.W), out _, out var Bu, out var Bv);
                    var t = Vector3d.Cross(Vector3d.Cross(Au, Av), Vector3d.Cross(Bu, Bv));
                    if (t.Length < 1e-12 * Math.Max(1, (Au.Length * Av.Length * Bu.Length * Bv.Length))) { Debug?.Invoke($"walk stop: tangent at {Pa:0.####}"); break; }   // касание
                    t = t.Normalized() * dir;
                    // предсказание в параметрах обеих поверхностей: S_u du + S_v dv = t·h (наименьшие квадраты)
                    Vector2d Pred(Vector3d su, Vector3d sv)
                    {
                        double a11 = Vector3d.Dot(su, su), a12 = Vector3d.Dot(su, sv), a22 = Vector3d.Dot(sv, sv), det = a11 * a22 - a12 * a12;
                        double b1 = Vector3d.Dot(su, t * h), b2 = Vector3d.Dot(sv, t * h);
                        return Math.Abs(det) < 1e-300 ? Vector2d.Zero : new Vector2d((b1 * a22 - b2 * a12) / det, (a11 * b2 - a12 * b1) / det);
                    }
                    var da = Pred(Au, Av); var db = Pred(Bu, Bv);
                    var qp = new Vector4d(q.X + da.X, q.Y + da.Y, q.Z + db.X, q.W + db.Y);
                    bool outside = qp.X < 0 || qp.X > 1 || qp.Y < 0 || qp.Y > 1 || qp.Z < 0 || qp.Z > 1 || qp.W < 0 || qp.W > 1;
                    var nq = Correct(A, B, new Vector4d(Math.Clamp(qp.X, 0, 1), Math.Clamp(qp.Y, 0, 1), Math.Clamp(qp.Z, 0, 1), Math.Clamp(qp.W, 0, 1)), Pa + t * h, t);
                    if (nq == null)
                    {
                        // поправка не сошлась: шаг вышел за область параметров (зажат на краю) — точка кривой на стороне грани
                        if (outside || !In(qp)) { pts.Add(ToBoundary(q, qp)); Debug?.Invoke($"walk stop: boundary (after a failed correction) at {A.PointAt(new Vector2d(pts[^1].X, pts[^1].Y)):0.####}"); break; }
                        Debug?.Invoke($"walk stop: correction failed at {Pa:0.####} step {step}"); break;
                    }
                    if (!In(nq.Value) || outside) { pts.Add(ToBoundary(q, nq.Value)); Debug?.Invoke($"walk stop: boundary at {A.PointAt(new Vector2d(pts[^1].X, pts[^1].Y)):0.####}"); break; }
                    q = nq.Value;
                    if (step > 4 && (A.PointAt(new Vector2d(q.X, q.Y)) - start3).Length < 0.8 * h) { closed = true; break; }
                    pts.Add(q);
                }
                return pts;
            }
            var fwd = Walk(1, out bool cl);
            List<Vector4d> curve;
            if (cl) { curve = fwd; curve.Add(fwd[0]); }
            else { var back = Walk(-1, out _); back.Reverse(); back.RemoveAt(back.Count - 1); curve = back; curve.AddRange(fwd); }
            if (curve.Count < 2) return;
            traced.Add(curve); foreach (var q in curve) tracedPts.Add(A.PointAt(new Vector2d(q.X, q.Y)));
        }
        Debug?.Invoke($"seeds {seeds.Count}");
        foreach (var s in seeds) TraceFrom(s);
        Debug?.Invoke($"traced {traced.Count}: " + string.Join(" | ", traced.Select(c => $"{c.Count} pts {A.PointAt(new Vector2d(c[0].X, c[0].Y)):0.###} → {A.PointAt(new Vector2d(c[^1].X, c[^1].Y)):0.###}")));
        string? Certify(List<Vector4d>? extra)
        {
            double rhoMin = 1e-4 * Math.Max(1, scale);
            var pa = NurbsTools.Beziers(A).Where(p => p.U1 > ra.U0 && p.U0 < ra.U1 && p.V1 > ra.V0 && p.V0 < ra.V1).ToList();
            var pb = NurbsTools.Beziers(B).Where(p => p.U1 > rb.U0 && p.U0 < rb.U1 && p.V1 > rb.V0 && p.V0 < rb.V1).ToList();
            var stack = new Stack<(BezierPatch P, BezierPatch Q)>();
            foreach (var x in pa) foreach (var y in pb) stack.Push((x, y));
            int visited = 0; string? fail = null;
            while (stack.Count > 0)
            {
                if (++visited > 400000) return "NURBS×NURBS certificate: too many cell pairs";
                var (P, Q) = stack.Pop();
                var (la, ha) = P.Box(); var (lb, hb) = Q.Box();
                if (la.X > hb.X || lb.X > ha.X || la.Y > hb.Y || lb.Y > ha.Y || la.Z > hb.Z || lb.Z > ha.Z) continue;   // врозь
                var ca = (la + ha) / 2; var cb = (lb + hb) / 2; double rA = (ha - la).Length / 2, rB = (hb - lb).Length / 2;
                if (Near(ca, rA + 2 * h) && Near(cb, rB + 2 * h)) continue;                                           // пояснена
                if (Math.Max(rA, rB) > rhoMin)
                {
                    if (rA >= rB) { var (x, y) = P.Split(P.U1 - P.U0 >= P.V1 - P.V0); stack.Push((x, Q)); stack.Push((y, Q)); }
                    else { var (x, y) = Q.Split(Q.U1 - Q.U0 >= Q.V1 - Q.V0); stack.Push((P, x)); stack.Push((P, y)); }
                    continue;
                }
                var q0 = new Vector4d(Math.Clamp((P.U0 + P.U1) / 2, ra.U0, ra.U1), Math.Clamp((P.V0 + P.V1) / 2, ra.V0, ra.V1), Math.Clamp((Q.U0 + Q.U1) / 2, rb.U0, rb.U1), Math.Clamp((Q.V0 + Q.V1) / 2, rb.V0, rb.V1));
                if (Correct(A, B, q0, null, null) is { } qq)
                {
                    if (!In(qq)) continue;
                    var x3 = A.PointAt(new Vector2d(qq.X, qq.Y));
                    if (Near(x3, 2 * h)) continue;
                    if (extra == null) return $"NURBS×NURBS certificate: an intersection point near {x3:0.######} is on no traced curve";
                    extra.Add(qq); fail ??= "new seeds"; continue;
                }
                return $"NURBS×NURBS certificate: unexplained near-contact cell pair at {ca:0.######}";
            }
            return fail;
        }
        for (int round = 0; round < 3; round++)
        {
            var extra = new List<Vector4d>();
            var why = Certify(extra);
            if (why == null) break;
            int before = traced.Count;
            foreach (var q in extra) TraceFrom(q);
            if (traced.Count == before || round == 2) { if (Certify(null) is { } w2) res.Uncertified = w2; break; }
        }
        foreach (var c in traced) if (c.Count >= 2) res.Curves.Add(new NurbsNurbsCurve3(A, B, c));
        res.Kind = res.Curves.Count > 0 ? Ssi.Kind.Curves : Ssi.Kind.Empty;
        return res;
    }
}

/// <summary>Кривая пересечения двух граней NURBS: ломаная в (u₁, v₁, u₂, v₂), точка — на первой поверхности после поправки
/// (S₁ = S₂ до 1e-12) в плоскости, поперечной хорде; параметр — доля длины пути.</summary>
public sealed class NurbsNurbsCurve3 : ICurve3
{
    public readonly NurbsSurface A, B;
    readonly Vector4d[] _q; readonly double[] _s; readonly Vector3d[] _p;
    public IReadOnlyList<Vector4d> Seed => _q;
    public NurbsNurbsCurve3(NurbsSurface a, NurbsSurface b, IReadOnlyList<Vector4d> q)
    {
        A = a; B = b; _q = q.ToArray(); _s = new double[_q.Length]; _p = _q.Select(x => A.PointAt(new Vector2d(x.X, x.Y))).ToArray();
        for (int i = 1; i < _q.Length; i++) _s[i] = _s[i - 1] + (_p[i] - _p[i - 1]).Length;
    }
    public double T0 => 0; public double T1 => 1;
    public bool IsClosed => _q.Length > 2 && (_p[0] - _p[^1]).Length < 1e-10;
    public double Length => _s[^1];
    public IEnumerable<double> Breaks { get { double tot = _s[^1]; if (tot <= 0) yield break; for (int i = 1; i < _s.Length - 1; i++) yield return _s[i] / tot; } }
    public Vector4d QAt(double t)
    {
        double tot = _s[^1]; if (tot <= 0) return _q[0];
        double L = Math.Clamp(t, 0, 1) * tot; int lo = 0, hi = _s.Length - 1;
        while (hi - lo > 1) { int m = (lo + hi) / 2; if (_s[m] <= L) lo = m; else hi = m; }
        double seg = _s[hi] - _s[lo], a = seg > 0 ? (L - _s[lo]) / seg : 0;
        if (a <= 0) return _q[lo]; if (a >= 1) return _q[hi];
        var q = _q[lo] + (_q[hi] - _q[lo]) * a; var x0 = _p[lo] + (_p[hi] - _p[lo]) * a; var tv = (_p[hi] - _p[lo]).Normalized();
        return NurbsNurbsSsi.Correct(A, B, q, x0, tv) ?? q;
    }
    public Vector3d PointAt(double t) { var q = QAt(t); return A.PointAt(new Vector2d(q.X, q.Y)); }
    public Vector3d TangentAt(double t) { double h = 1e-6; var d = PointAt(Math.Min(1, t + h)) - PointAt(Math.Max(0, t - h)); return d.LengthSquared < 1e-30 ? Vector3d.UnitX : d.Normalized(); }
    public Bounds3 Bounds
    {
        get
        {
            var b = Bounds3.Empty; foreach (var p in _p) b.Add(p);
            double step = 0; for (int i = 1; i < _s.Length; i++) step = Math.Max(step, _s[i] - _s[i - 1]);
            b.Add(b.Min - new Vector3d(step)); b.Add(b.Max + new Vector3d(step)); return b;
        }
    }
    public ICurve3 Reversed() => new NurbsNurbsCurve3(A, B, _q.Reverse().ToList());
    public void Tessellate(List<Vector3d> dst, bool includeFirstPoint) { for (int i = includeFirstPoint ? 0 : 1; i < _p.Length; i++) dst.Add(_p[i]); }
}
