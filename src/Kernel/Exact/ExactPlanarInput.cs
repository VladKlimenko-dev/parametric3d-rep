using System.Numerics;
using OpenTK.Mathematics;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Surfaces;

namespace Parametric3D.Kernel.Exact;

/// <summary>
/// ВХОД В ТОЧНОЕ ЯДРО «ОДНА ПЛОСКАЯ ГРАНЬ — ОДНА ТОЧНАЯ ПЛОСКОСТЬ».
///
/// Прежний вход округляет каждую вершину на сетку и строит плоскость каждого треугольника через три
/// округлённые точки. У грани, не параллельной осям, четыре округлённых угла в общем случае не лежат в
/// одной плоскости, и грань становится двумя-тремя почти совпадающими точными плоскостями (расхождение —
/// несколько шагов сетки). Следующая операция прижимается к одной из них, и между копиями остаётся клин:
/// неманифолдное ребро, лишние вершины в микрометре друг от друга, тонкие грани-осколки
/// (класс 2 повёрнутого скругления, FProbe 3D axis=(0.2,0.7,0.4) angle=0.9).
///
/// Здесь у каждой плоской грани своя плоскость: нормаль — средняя по треугольникам, квантованная до 2^-30,
/// смещение — через округлённый центр грани. Вершина — пересечение плоскостей её граней (не больше трёх
/// независимых); недостающие степени свободы — координаты сетки, как прежде. Грань, плоскость которой
/// не удаётся выдержать во всех её вершинах (вершина с несовместными четырьмя плоскостями, сдвиг больше
/// поправки), лишается своей плоскости, и решение повторяется.
///
/// Тела, грани которых параллельны осям, дают те же узлы сетки, что и прежний вход; тогда возвращается
/// null и вызывающий идёт прежней дорогой — побитово как раньше, с той же нумерацией реестра.
/// </summary>
internal static class ExactPlanarInput
{
    /// <summary>Выключение для сравнения с прежним входом: P3D_PLANARINPUT=0.</summary>
    private static bool _gEnabled = Environment.GetEnvironmentVariable("P3D_PLANARINPUT") != "0";
    /// <summary>Поставка 2: значение контекста вычисления (KernelSettings.PlanarInput), если он задан; иначе общее по умолчанию.</summary>
    internal static bool Enabled
    {
        get => Parametric3D.Kernel.KernelContext.Current is { } kc ? kc.Settings.PlanarInput : _gEnabled;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gEnabled, value, "ExactPlanarInput.Enabled");
    }

    /// <summary>Трасса решения (только для разбора; null — выключено).</summary>
    [ThreadStatic] internal static Action<string>? Trace;

    /// <summary>Последнее решение: грани без своей плоскости, повторы (для проверок).</summary>
    [ThreadStatic] internal static (int Faces, int OwnPlanes, int Dropped, int Rounds, int Moved) LastStats;

    internal readonly record struct P(BigRational X, BigRational Y, BigRational Z)
    {
        public static P operator +(P a, P b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static P operator -(P a, P b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public BigRational Dot(P b) => X * b.X + Y * b.Y + Z * b.Z;
        public P Cross(P b) => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
        public Vector3d Mm => new(X.ToDouble() / ExactGrid.UnitsPerMm, Y.ToDouble() / ExactGrid.UnitsPerMm,
                                  Z.ToDouble() / ExactGrid.UnitsPerMm);
        public bool IsGrid => X.Den.IsOne && Y.Den.IsOne && Z.Den.IsOne;
        public GridPoint Grid => new((long)X.Num, (long)Y.Num, (long)Z.Num);
    }

    /// <summary>Наибольший допустимый сдвиг вершины от её точки в double (как у контактного входа).</summary>
    internal static readonly double Limit = 2 * Math.Sqrt(3) / ExactGrid.UnitsPerMm;

    /// <summary>Допуск аналитического совпадения плоскостей разных граней (мм).</summary>
    internal const double Coincident = 1e-7;

    /// <summary>
    /// Точные вершины треугольников. Возвращает null, если ни одна вершина не сдвинулась с узла
    /// сетки (прежний вход даст то же самое) или решение не удалось.
    /// </summary>
    /// <param name="hard">Плоскости, заданные извне (контакт с телом) по меткам граней; их вершины
    /// обязаны лежать на них точно, иначе отказ.</param>
    internal static P[]? Solve(PlaneRegistry r, List<CsgPolygon> tris, Dictionary<uint, ISurface3?> sources,
                               out int[][] corners, IReadOnlyDictionary<uint, int>? hard = null,
                               bool ownPlanes = true, PlaneRegistry? target = null)
        => Solve(r, tris, sources, out corners, out _, hard, ownPlanes, target);

    /// <param name="hints">Истинные плоскости построенных своих плоскостей — для записи в целевой реестр
    /// (PlaneRegistry.FaceHints), если вызывающий примет результат.</param>
    internal static P[]? Solve(PlaneRegistry r, List<CsgPolygon> tris, Dictionary<uint, ISurface3?> sources,
                               out int[][] corners,
                               out List<(BigInteger A, BigInteger B, BigInteger C, BigInteger D, Vector3d N, double W)> hints,
                               IReadOnlyDictionary<uint, int>? hard = null,
                               bool ownPlanes = true, PlaneRegistry? target = null)
    {
        corners = Array.Empty<int[]>();
        hints = new();

        // ПОСТАВКА 3. РАННИЙ ОТКАЗ ДО СКЛЕЙКИ ТОЧЕК. Если у каждой плоской грани квантованная нормаль вдоль оси,
        // все свои плоскости проходят через узлы сетки и решение кончится тем же отказом (прежний вход даст то же
        // самое) — но прежде оно успевало склеить все точки и построить плоскости (12.6 мс на 18 вызовов в
        // перестроении реалистичной детали). Подсказка реестра с почти осевой, но не осевой нормалью могла бы
        // заменить плоскость грани — тогда ранний отказ не применяется, и ответ остаётся прежним.
        if (hard == null && ownPlanes && AllPlanarFacesAxisAligned(tris, sources, target)) { LastStats = default; return null; }
        // ---- точки: склейка ближе полушага сетки (как у прежнего входа), один представитель
        double tau = 0.5 / ExactGrid.UnitsPerMm;
        var cells = new Dictionary<(long, long, long), List<int>>();
        var pd = new List<Vector3d>();
        int Index(Vector3d p)
        {
            long cx = (long)Math.Floor(p.X / tau), cy = (long)Math.Floor(p.Y / tau), cz = (long)Math.Floor(p.Z / tau);
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                    for (long dz = -1; dz <= 1; dz++)
                        if (cells.TryGetValue((cx + dx, cy + dy, cz + dz), out var list))
                            foreach (int q in list)
                                if ((pd[q] - p).LengthSquared <= tau * tau) return q;
            if (!cells.TryGetValue((cx, cy, cz), out var own)) cells[(cx, cy, cz)] = own = new List<int>();
            own.Add(pd.Count); pd.Add(p);
            return pd.Count - 1;
        }
        corners = new int[tris.Count][];
        for (int t = 0; t < tris.Count; t++)
        {
            var v = tris[t].V;
            if (v.Count != 3) return null;
            foreach (var p in v)
                if (!double.IsFinite(p.X) || !double.IsFinite(p.Y) || !double.IsFinite(p.Z)) return null;
            corners[t] = new[] { Index(v[0]), Index(v[1]), Index(v[2]) };
        }
        int n = pd.Count;
        var grid = new P[n];
        for (int i = 0; i < n; i++)
        {
            var g = GridPoint.FromMm(pd[i].X, pd[i].Y, pd[i].Z);
            grid[i] = new P(g.X, g.Y, g.Z);
        }

        // ---- плоскость каждой плоской грани
        var facePlane = new Dictionary<uint, int>();
        var isHard = new HashSet<uint>();
        var faceVerts = new Dictionary<uint, HashSet<int>>();
        for (int t = 0; t < tris.Count; t++)
        {
            if (!faceVerts.TryGetValue(tris[t].Tag, out var set)) faceVerts[tris[t].Tag] = set = new HashSet<int>();
            set.UnionWith(corners[t]);
        }
        if (hard != null)
            foreach (var kv in hard) { facePlane[kv.Key] = kv.Value; isHard.Add(kv.Key); }
        // Совпадающие (после квантования) плоскости разных граней — одна плоскость: иначе вершина на их
        // стыке получила бы две почти параллельные плоскости и ушла бы вдоль их далёкой общей прямой.
        // Известные истинные плоскости: подсказки целевого реестра (другие операнды) и свои, по мере построения.
        var known = new List<(Vector3d N, double W, (BigInteger A, BigInteger B, BigInteger C, BigInteger D) Coeffs)>();
        int reused = 0;
        if (target != null && ownPlanes)
            foreach (var h in target.FaceHints) known.Add((h.N, h.W, (h.A, h.B, h.C, h.D)));
        int own = 0;
        if (ownPlanes)
            foreach (var group in tris.GroupBy(p => p.Tag).OrderBy(g => g.Key))
            {
                if (facePlane.ContainsKey(group.Key)) continue;
                if (!sources.TryGetValue(group.Key, out var surface) || surface?.IsPlanar != true) continue;
                var nd = Vector3d.Zero;
                foreach (var poly in group) nd += Vector3d.Cross(poly.V[1] - poly.V[0], poly.V[2] - poly.V[0]);
                if (nd.LengthSquared < 1e-24) continue;
                nd = nd.Normalized();
                const double S = 1 << 30;
                BigInteger a = new(Math.Round(nd.X * S)), b = new(Math.Round(nd.Y * S)), c = new(Math.Round(nd.Z * S));
                if (a.IsZero && b.IsZero && c.IsZero) continue;
                var gcd = BigInteger.GreatestCommonDivisor(BigInteger.GreatestCommonDivisor(a, b), c);
                if (gcd > 1) { a /= gcd; b /= gcd; c /= gcd; }
                var ids = faceVerts[group.Key];
                var centre = ids.Aggregate(Vector3d.Zero, (acc, i) => acc + pd[i]) / ids.Count;
                var gc = GridPoint.FromMm(centre.X, centre.Y, centre.Z);
                var d = -(a * gc.X + b * gc.Y + c * gc.Z);
                // АНАЛИТИЧЕСКИ СОВПАДАЮЩАЯ ПЛОСКОСТЬ УЖЕ ЕСТЬ — берётся она. Сравниваются ИСТИННЫЕ плоскости
                // (нормаль и смещение по точкам грани в double), а не точные: точная проходит через округлённый
                // центр и отстоит от истинной на долю шага сетки. Совпадение — все вершины грани в пределах
                // Coincident (1e-7 мм) от истинной плоскости другой грани этого тела или другого операнда в
                // том же реестре (FProbe Z angle=0.1 union=True: копии одной плоскости через разные центры
                // расходились на 1e-6 мм, и стык операндов рассыпался на пары вершин). Настоящий зазор от
                // 1e-7 мм сохраняется.
                double trueW = ids.Average(i => Vector3d.Dot(nd, pd[i]));
                int plane = -1;
                foreach (var (kn, kw, co) in known)
                {
                    double dot = Vector3d.Dot(kn, nd);
                    if (Math.Abs(dot) < 1 - 1e-12) continue;
                    double sgn = Math.Sign(dot);
                    if (ids.All(i => Math.Abs(sgn * Vector3d.Dot(kn, pd[i]) - sgn * kw) <= Coincident))
                    {
                        plane = r.Plane(co.A, co.B, co.C, co.D).Id;
                        reused++;
                        break;
                    }
                }
                if (plane < 0)
                {
                    plane = r.Plane(a, b, c, d).Id;
                    var co = r.Coefficients(plane);
                    known.Add((nd, trueW, co));
                    hints.Add((co.A, co.B, co.C, co.D, nd, trueW));
                }
                var (ux, uy, uz) = r.UnitNormal(new OPlane(plane, 1));
                var un = new Vector3d(ux, uy, uz);
                var cm = new Vector3d(gc.X, gc.Y, gc.Z) / ExactGrid.UnitsPerMm;
                if (ids.Any(i => Math.Abs(Vector3d.Dot(un, pd[i] - cm)) > Limit))
                {
                    Trace?.Invoke($"face {group.Key}: not flat within {Limit:E1} mm");
                    continue;
                }
                facePlane[group.Key] = plane;
                own++;
                Trace?.Invoke($"face {group.Key} {surface?.GetType().Name}: n ({nd.X:R},{nd.Y:R},{nd.Z:R}) q ({a},{b},{c}) verts {ids.Count}");
            }
        // Все свои плоскости параллельны осям (a·b·c — один ненулевой коэффициент) и внешних нет: каждая проходит
        // через узел сетки, вершины выйдут на прежние узлы — решать незачем, прежний вход даст то же самое.
        if (hard == null && own > 0 && facePlane.Values.Distinct().All(pl =>
            { var (a, b, c, _) = r.Coefficients(pl); return (a.IsZero ? 0 : 1) + (b.IsZero ? 0 : 1) + (c.IsZero ? 0 : 1) == 1; }))
        {
            LastStats = (faceVerts.Count, own, 0, 0, 0);
            return null;
        }
        if (own == 0)
        {
            LastStats = (faceVerts.Count, 0, 0, 0, 0);
            if (hard == null) return null;
        }

        // ---- грани каждой вершины (по порядку меток — решение детерминировано)
        var vertexFaces = new List<uint>[n];
        for (int i = 0; i < n; i++) vertexFaces[i] = new List<uint>();
        foreach (var (tag, set) in faceVerts.OrderBy(kv => kv.Key))
            foreach (int i in set) vertexFaces[i].Add(tag);

        // ВЕРШИНА ГРАНЁНОЙ КРИВОЙ ГРАНИ ОСТАЁТСЯ НА УЗЛЕ СЕТКИ. Её точка — точка хорды кривой; восстановление
        // дуг по хордам (BooleanOp) и точные цилиндрические пути рассчитаны на прежний узел. Сдвиг по плоскости
        // торца резца на доли шага рвал цепочки хорд (K01 S-ROUNDHOLE: контур 3 мм). Плоская грань, не
        // выдерживающая из-за этого свою плоскость, её теряет (ниже) — прежний вход для неё.
        var curved = new bool[n];
        foreach (var (tag, set) in faceVerts)
            if (!facePlane.ContainsKey(tag) && (!sources.TryGetValue(tag, out var sf) || sf?.IsPlanar != true))
                foreach (int i in set) curved[i] = true;

        var result = new P[n];
        int dropped = 0, rounds = 0;
        while (true)
        {
            rounds++;
            var drop = new HashSet<uint>();
            for (int i = 0; i < n; i++)
            {
                var hardPlanes = new List<int>();
                var ownList = new List<(int Plane, uint Tag)>();
                foreach (uint tag in vertexFaces[i])
                    if (facePlane.TryGetValue(tag, out int pl))
                    {
                        if (isHard.Contains(tag)) { if (!hardPlanes.Contains(pl)) hardPlanes.Add(pl); }
                        else if (!ownList.Any(o => o.Plane == pl)) ownList.Add((pl, tag));
                    }
                if (hardPlanes.Count == 0 && ownList.Count == 0) { result[i] = grid[i]; continue; }
                var selected = new List<int>(); P n0 = default, dir = default;
                Vector3d n0d = default;
                bool Take(int plane)
                {
                    var (a, b, c, _) = r.Coefficients(plane); var nn = new P(a, b, c);
                    var (ux, uy, uz) = r.UnitNormal(new OPlane(plane, 1)); var nd = new Vector3d(ux, uy, uz);
                    if (selected.Count == 0) { selected.Add(plane); n0 = nn; n0d = nd; return true; }
                    if (selected.Count == 1)
                    {
                        var cross = n0.Cross(nn);
                        // Почти параллельные плоскости — не пара: их прямая плохо обусловлена.
                        if (cross.Dot(cross).Sign != 0 && Vector3d.Cross(n0d, nd).Length > 1e-6)
                        { selected.Add(plane); dir = cross; return true; }
                        return false;
                    }
                    if (selected.Count == 2 && dir.Dot(nn).Sign != 0)
                    {
                        var dd = dir.Mm; // только направление
                        if (Math.Abs(Vector3d.Dot(dd.Normalized(), nd)) > 1e-6) { selected.Add(plane); return true; }
                    }
                    return false;
                }
                foreach (int pl in hardPlanes.Order()) Take(pl);
                if (!curved[i]) foreach (var (pl, _) in ownList) Take(pl);
                if (selected.Count == 0)
                {
                    // Только кривые грани и свои плоскости, которые не взяты: прежний узел сетки.
                    var gv = r.Vertex(grid[i].Grid);
                    foreach (var (pl, tag) in ownList) if (r.Side(gv, pl) != 0) drop.Add(tag);
                    result[i] = grid[i];
                    continue;
                }
                int Largest(P p)
                {
                    var a = BigInteger.Abs(p.X.Num * p.Y.Den * p.Z.Den);
                    var b = BigInteger.Abs(p.Y.Num * p.X.Den * p.Z.Den);
                    var c = BigInteger.Abs(p.Z.Num * p.X.Den * p.Y.Den);
                    return a >= b && a >= c ? 0 : b >= c ? 1 : 2;
                }
                void Coordinate(int axis)
                {
                    var p = grid[i]; var value = axis == 0 ? p.X : axis == 1 ? p.Y : p.Z;
                    selected.Add(r.Plane(axis == 0 ? 1 : 0, axis == 1 ? 1 : 0, axis == 2 ? 1 : 0, -value.Num).Id);
                }
                if (selected.Count == 1)
                {
                    int pivot = Largest(n0);
                    for (int axis = 0; axis < 3; axis++) if (axis != pivot) Coordinate(axis);
                }
                else if (selected.Count == 2) Coordinate(Largest(dir));
                int vertex = r.Vertex(selected[0], selected[1], selected[2]);
                if (hardPlanes.Any(pl => r.Side(vertex, pl) != 0)) { Trace?.Invoke($"vertex {i}: off a contact plane"); return null; }
                var (x, y, z, w) = r.Homogeneous(vertex);
                var q = new P(new BigRational(x, w), new BigRational(y, w), new BigRational(z, w));
                bool far = (q.Mm - pd[i]).Length > Limit;
                foreach (var (pl, tag) in ownList)
                    if (far || r.Side(vertex, pl) != 0) drop.Add(tag);
                if (far && ownList.Count == 0) { Trace?.Invoke($"vertex {i}: contact moved it {(q.Mm - pd[i]).Length:E1} mm"); return null; }
                result[i] = q;
            }
            if (drop.Count == 0) break;
            foreach (uint tag in drop)
            {
                Trace?.Invoke($"face {tag}: own plane dropped");
                facePlane.Remove(tag); dropped++;
            }
            if (rounds > 16) return null;
        }
        int moved = 0;
        for (int i = 0; i < n; i++) if (result[i] != grid[i]) moved++;
        LastStats = (faceVerts.Count, own, dropped, rounds, moved);
        Trace?.Invoke($"faces {faceVerts.Count}, own planes {own} (reused {reused}), dropped {dropped}, rounds {rounds}, vertices off grid {moved} of {n}");
        if (moved == 0 && hard == null) return null;
        return result;
    }

    private static bool AllPlanarFacesAxisAligned(List<CsgPolygon> tris, Dictionary<uint, ISurface3?> sources, PlaneRegistry? target)
    {
        const double S = 1 << 30;
        var sums = new Dictionary<uint, Vector3d>();
        foreach (var t in tris)
        {
            if (t.V.Count != 3) return false;
            if (!sources.TryGetValue(t.Tag, out var surface) || surface?.IsPlanar != true) continue;
            sums[t.Tag] = sums.GetValueOrDefault(t.Tag) + Vector3d.Cross(t.V[1] - t.V[0], t.V[2] - t.V[0]);
        }
        foreach (var nd0 in sums.Values)
        {
            if (nd0.LengthSquared < 1e-24) continue;                 // такой грани своя плоскость не даётся
            var nd = nd0.Normalized();
            int nonzero = (Math.Round(nd.X * S) != 0 ? 1 : 0) + (Math.Round(nd.Y * S) != 0 ? 1 : 0) + (Math.Round(nd.Z * S) != 0 ? 1 : 0);
            if (nonzero != 1) return false;
        }
        if (target != null)
            foreach (var h in target.FaceHints)
            {
                double m = Math.Max(Math.Abs(h.N.X), Math.Max(Math.Abs(h.N.Y), Math.Abs(h.N.Z)));
                int nonzero = (h.A.IsZero ? 0 : 1) + (h.B.IsZero ? 0 : 1) + (h.C.IsZero ? 0 : 1);
                if (nonzero > 1 && m > 1 - 1e-12) return false;          // почти осевая, но не осевая подсказка
            }
        return true;
    }

    /// <summary>Многоугольники по точным вершинам: опорная — через три вершины, стороны — перпендикулярно.</summary>
    internal static List<ExactPolygon> Triangles(PlaneRegistry r, List<CsgPolygon> tris, P[] points, int[][] corners)
    {
        OPlane Plane(P nn, BigRational d)
        {
            BigInteger scale = 1;
            foreach (var v in new[] { nn.X, nn.Y, nn.Z, d })
                scale = scale / BigInteger.GreatestCommonDivisor(scale, v.Den) * v.Den;
            return r.Plane(nn.X.Num * (scale / nn.X.Den), nn.Y.Num * (scale / nn.Y.Den),
                           nn.Z.Num * (scale / nn.Z.Den), d.Num * (scale / d.Den));
        }
        var polys = new List<ExactPolygon>(tris.Count);
        for (int t = 0; t < tris.Count; t++)
        {
            var a = points[corners[t][0]]; var b = points[corners[t][1]]; var c = points[corners[t][2]];
            var nn = (b - a).Cross(c - a);
            if (nn.Dot(nn).Sign == 0) continue;
            var support = Plane(nn, -nn.Dot(a));
            OPlane Bound(P u, P v, P inside)
            {
                var en = nn.Cross(v - u); var d = -en.Dot(u); var plane = Plane(en, d);
                return (en.Dot(inside) + d).Sign < 0 ? plane : plane.Flipped;
            }
            polys.Add(new ExactPolygon(support, new[] { Bound(a, b, c), Bound(b, c, a), Bound(c, a, b) }, tris[t].Tag));
        }
        return polys;
    }
}
