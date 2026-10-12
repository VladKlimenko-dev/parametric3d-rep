using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Ops;

/// <summary>
/// ФАСКА И СКРУГЛЕНИЕ ОДНОЙ ВЕРШИНЫ — для угла из N ≥ 3 прямых рёбер и N плоских граней,
/// ВЫПУКЛОГО (тело — выпуклый конус: наружный угол) или ВОГНУТОГО (пустота — выпуклый конус,
/// тело вокруг: внутренний угол кармана, паза, выреза). У выпуклого угла материал срезается,
/// у вогнутого — та же область добавляется (фаска-заполнение, вогнутая сферическая поверхность).
/// Седловая вершина (рёбра и выпуклые, и вогнутые) не поддержана — отказ с причиной.
///
/// ФАСКА срезает угол по точкам Qᵢ на рёбрах на расстояниях dᵢ от вершины (равномерная — все dᵢ
/// равны). Срезается выпуклая оболочка вершины и точек Qᵢ. Если точки лежат в одной плоскости
/// (три ребра — всегда; правильная пирамида при равных dᵢ), срез — одна плоская грань; если нет —
/// несколько плоских граней по нижним граням оболочки (тело остаётся выпуклым у угла).
///
/// СКРУГЛЕНИЕ радиусом R заменяет угол частью сферы радиуса R. Центр сферы — на оси угла:
/// направлении, одинаково наклонённом ко всем рёбрам (если рёбра лежат на круговом конусе —
/// у трёх рёбер всегда, у правильной пирамиды тоже), иначе — среднем направлении рёбер. Сфера
/// касается рёбер, наиболее удалённых от оси, и пересекает остальные (там дуги на гранях
/// встречают ребро под углом). На каждой грани срез — дуга окружности. Срезается часть
/// выпуклой оболочки «вершина + точки, где сфера встречает рёбра» вне сферы.
/// </summary>
public static class VertexOp
{
    /// <summary>Запас радиуса шара-резца (доля R) для трансверсального касания рёбер.</summary>
    private static double _gBallMargin = 2e-6;
    /// <summary>Поставка 2: значение контекста вычисления (KernelSettings.VertexBallMargin), если он задан; иначе общее по умолчанию.</summary>
    public static double BallMargin
    {
        get => Parametric3D.Kernel.KernelContext.Current is { } kc ? kc.Settings.VertexBallMargin : _gBallMargin;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gBallMargin, value, "VertexOp.BallMargin");
    }

    /// <summary>
    /// Угол: вершина, рёбра (направления от вершины), их длины и концы, а также ВОГНУТ ли он
    /// (тело снаружи выпуклого конуса из рёбер: внутренний угол кармана, паза, выреза) и
    /// наружные нормали граней угла.
    /// </summary>
    public sealed record Corner(TVertex Vertex, Vector3d P, Vector3d[] Dirs, double[] Lengths, Vector3d[] Far,
                                bool Concave = false, Vector3d[]? FaceNormals = null);

    /// <summary>Угол у точки: вершина тела ближе tol, N ≥ 3 прямых рёбер, грани плоские, угол выпуклый.</summary>
    public static Corner Find(TSolid body, Vector3d point, double tol = 1e-3)
    {
        var verts = body.Edges.SelectMany(e => new[] { e.V0, e.V1 }).Distinct().ToList();
        var v = verts.OrderBy(x => (x.P - point).LengthSquared).FirstOrDefault()
                ?? throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Vertex: the body has no vertices.");
        if ((v.P - point).Length > tol) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, $"Vertex: no vertex at the selected point (nearest is {(v.P - point).Length:0.###} mm away).");
        // Вершина из точного тела может распасться на несколько близких вершин с микрорёбрами
        // между ними (плоскости через неё сходятся не точно в одной точке сетки — у вершины
        // пятигранной пирамиды). Такие вершины (ближе 1e-4 мм) считаются одной: рёбра угла —
        // те, что выходят из группы; микрорёбра внутри группы не в счёт.
        var cluster = new HashSet<TVertex>(ReferenceEqualityComparer.Instance) { v };
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var e in body.Edges)
            {
                if ((e.V0.P - e.V1.P).Length > 1e-4) continue;
                if (cluster.Contains(e.V0) ^ cluster.Contains(e.V1)) { cluster.Add(e.V0); cluster.Add(e.V1); grew = true; }
            }
        }
        var edges = body.Edges.Where(e => cluster.Contains(e.V0) ^ cluster.Contains(e.V1)).ToList();
        var centreP = cluster.Aggregate(Vector3d.Zero, (acc, x) => acc + x.P) / cluster.Count;
        if (edges.Count < 3) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, $"Vertex: {edges.Count} edges meet at the vertex; a corner needs at least three.");
        if (edges.Any(e => e.Curve is not LineCurve3)) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.Unsupported, "Vertex: all edges at the vertex must be straight.");
        var faces = edges.SelectMany(e => e.CoEdges).Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList();
        foreach (var f in faces)
        {
            var src = f.Surface is FacetedSurface fs ? fs.Source : f.Surface;
            if (src == null || !src.IsPlanar) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.Unsupported, "Vertex: all faces at the vertex must be planar.");
        }
        var far = edges.Select(e => cluster.Contains(e.V0) ? e.V1.P : e.V0.P).ToArray();
        var dirs = far.Select(q => (q - centreP).Normalized()).ToArray();
        var lens = far.Select(q => (q - centreP).Length).ToArray();
        // Выпуклый угол: каждая грань оставляет все рёбра угла по свою внутреннюю сторону (тело —
        // выпуклый конус). Вогнутый: все рёбра — по наружную сторону каждой грани (пустота —
        // выпуклый конус, тело вокруг него). Иначе — седловая вершина, для неё нет одной сферы
        // или плоскости, касающейся угла.
        var normals = faces.Select(f => f.NormalAt(centreP).Normalized()).ToArray();
        bool convex = normals.All(n => dirs.All(d => Vector3d.Dot(n, d) <= 1e-6));
        bool concave = normals.All(n => dirs.All(d => Vector3d.Dot(n, d) >= -1e-6));
        if (!convex && !concave)
            throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Vertex: the corner is neither convex nor concave (a saddle vertex); a vertex chamfer or round is not defined for it.");
        return new Corner(v, centreP, dirs, lens, far, Concave: !convex, FaceNormals: normals);
    }

    /// <summary>Размеры по рёбрам угла по точкам-подсказкам на рёбрах.</summary>
    public static double[] MatchDistances(Corner c, IReadOnlyList<(Vector3d OnEdge, double D)> perEdge)
    {
        int n = c.Dirs.Length;
        if (perEdge.Count != n) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, $"Vertex chamfer: {n} distances (one per edge) are needed, {perEdge.Count} given.");
        var d = new double[n]; var used = new bool[n];
        foreach (var (q, dist) in perEdge)
        {
            int best = -1; double bd = double.MaxValue;
            for (int i = 0; i < n; i++)
            {
                if (used[i]) continue;
                var a = c.P; var ab = c.Far[i] - a;
                double t = Math.Clamp(Vector3d.Dot(q - a, ab) / ab.LengthSquared, 0, 1);
                double dd = (a + ab * t - q).Length;
                if (dd < bd) { bd = dd; best = i; }
            }
            used[best] = true; d[best] = dist;
        }
        return d;
    }

    private static TSolid BodyAt(List<TSolid> bodies, Vector3d point, string what) =>
        bodies.FirstOrDefault(b => b.Edges.Any(e => (e.V0.P - point).Length < 1e-3 || (e.V1.P - point).Length < 1e-3))
        ?? throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, $"{what}: no vertex at the selected point.");

    public static void Chamfer(List<TSolid> bodies, Vector3d point, double[] d, List<string> warnings)
    {
        if (bodies.Count == 0) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Vertex chamfer: no body.");
        var c = Find(BodyAt(bodies, point, "Vertex chamfer"), point);
        int n = c.Dirs.Length;
        if (d.Length == 1) d = Enumerable.Repeat(d[0], n).ToArray();
        if (d.Length != n) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, $"Vertex chamfer: {n} distances are needed.");
        for (int i = 0; i < n; i++)
        {
            if (d[i] <= Tol.Weld) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Vertex chamfer: distances must be greater than zero.");
            if (d[i] >= c.Lengths[i] - Tol.Weld) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, $"Vertex chamfer: distance {d[i]:0.###} does not fit on an edge of length {c.Lengths[i]:0.###}.");
        }
        var q = Enumerable.Range(0, n).Select(i => c.P + c.Dirs[i] * d[i]).ToArray();
        var tool = CornerRegion(c, q, warnings) ?? throw new RebuildException(Parametric3D.Kernel.OperationOutcome.Unsupported, "Vertex chamfer: the cut region could not be built.");
        // Вогнутый угол — фаска ДОБАВЛЯЕТ материал: заполняется та же оболочка «вершина + точки».
        if (c.Concave) BooleanOp.Unite(bodies, tool, warnings); else BooleanOp.Subtract(bodies, tool, warnings);
        warnings.Add($"Vertex chamfer ({(c.Concave ? "concave, material added" : "convex")}): {n} edges, {string.Join(" × ", d.Select(x => x.ToString("0.###")))}.");
    }

    public static void Round(List<TSolid> bodies, Vector3d point, double r, List<string> warnings)
    {
        if (bodies.Count == 0) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Vertex round: no body.");
        if (r <= Tol.Weld) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Vertex round: the radius must be greater than zero.");
        var c = Find(BodyAt(bodies, point, "Vertex round"), point);
        int n = c.Dirs.Length;
        var (centre, cut) = RoundGeometry(c, r);
        for (int i = 0; i < n; i++)
            if (cut[i] >= c.Lengths[i] - Tol.Weld) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, $"Vertex round: radius {r:0.###} needs a setback of {cut[i]:0.###} that does not fit on an edge of length {c.Lengths[i]:0.###}.");
        var q = Enumerable.Range(0, n).Select(i => c.P + c.Dirs[i] * cut[i]).ToArray();   // где сфера встречает рёбра
        // Шар-резец чуть больше (на 2e-6 R): точное касание сферы и ребра — вырожденный случай
        // для многогранной булевой (касание ребра вершиной гранёного шара давало у точки касания
        // микрорёбра и незамкнутые контуры — найдено проверкой R5). С таким запасом шар пересекает
        // ребро трансверсально в ~0.002R от точки касания; отклонение формы — 2e-6 R.
        // Не меньше такого, чтобы точка пересечения отошла от точки касания на 1e-3 мм (в 100 раз
        // больше сетки точной арифметики): при 2e-7 R проверка уже ловила незамкнутый контур.
        double margin = Math.Max(BallMargin, 0.5 * Math.Pow(1e-3 / r, 2));
        var ball = PrimitiveOp.Sphere(centre, r * (1 + margin));
        var region = CornerRegion(c, q, warnings) ?? throw new RebuildException(Parametric3D.Kernel.OperationOutcome.Unsupported, "Vertex round: the corner region could not be built.");
        var tool = new List<TSolid> { region };
        BooleanOp.Subtract(tool, ball, warnings);
        if (tool.Count == 0) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Vertex round: nothing to change.");
        // Вогнутый угол — та же область вне шара ДОБАВЛЯЕТСЯ: внутренний угол становится вогнутой
        // сферической поверхностью радиуса R.
        if (c.Concave) BooleanOp.Unite(bodies, tool[0], warnings); else BooleanOp.Subtract(bodies, tool[0], warnings);
        warnings.Add($"Vertex round ({(c.Concave ? "concave, material added" : "convex")}): {n} edges, R {r:0.###}, setbacks {string.Join("/", cut.Select(x => x.ToString("0.###")))}.");
    }

    /// <summary>
    /// Ось угла, центр сферы и расстояния от вершины до точек, где сфера встречает рёбра.
    /// Ось a: решение eᵢ·a = 1 по наименьшим квадратам (у рёбер на круговом конусе — точное).
    /// Центр c = P + λa, λ = R / sin θmax (θᵢ — угол ребра к оси): сфера касается самых
    /// удалённых от оси рёбер и пересекает остальные; точка встречи — ближняя к вершине.
    /// </summary>
    public static (Vector3d Centre, double[] Cut) RoundGeometry(Corner c, double r)
    {
        var e = c.Dirs;
        // (Σ eᵢeᵢᵀ) a = Σ eᵢ.
        var M = new double[3, 3]; var b = Vector3d.Zero;
        foreach (var ei in e)
        {
            b += ei;
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) M[i, j] += ei[i] * ei[j];
        }
        var a = Solve(M, b);
        if (a.LengthSquared < 1e-18 || e.Any(ei => Vector3d.Dot(ei, a) <= 0)) a = b;
        a = a.Normalized();
        double sinMax = e.Max(ei => Math.Sqrt(Math.Max(0, 1 - Math.Pow(Vector3d.Dot(ei, a), 2))));
        double lambda = r / Math.Max(1e-12, sinMax);
        var centre = c.P + a * lambda;
        var cut = e.Select(ei =>
        {
            double k = Vector3d.Dot(ei, a);
            double dist2 = lambda * lambda * Math.Max(0, 1 - k * k);
            return lambda * k - Math.Sqrt(Math.Max(0, r * r - dist2));
        }).ToArray();
        return (centre, cut);
    }

    /// <summary>
    /// Резец угла: выпуклая оболочка вершины и точек на рёбрах — по сторону вершины от каждой её
    /// «нижней» грани (не проходящей через вершину), в пределах куба около угла. Тело у вершины —
    /// выпуклый N-гранный угол, и его пересечение с этой областью — ровно оболочка.
    /// </summary>
    private static TSolid? CornerRegion(Corner c, Vector3d[] q, List<string> warnings)
    {
        var p = c.P;
        var planes = new List<(Vector3d At, Vector3d Inward)>();
        // У вогнутого угла область ограничивается и гранями угла (со стороны пустоты): иначе
        // объединение добавило бы материал за гранями, где тела нет.
        if (c.Concave && c.FaceNormals != null)
            foreach (var nf in c.FaceNormals) planes.Add((p, nf));
        int n = q.Length;
        double scale = q.Max(x => (x - p).Length);
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                for (int k = j + 1; k < n; k++)
                {
                    var nrm = Vector3d.Cross(q[j] - q[i], q[k] - q[i]);
                    if (nrm.Length < 1e-12 * scale * scale) continue;
                    nrm = nrm.Normalized();
                    double sp = Vector3d.Dot(nrm, p - q[i]);
                    if (Math.Abs(sp) < 1e-9 * scale) continue;
                    if (sp < 0) { nrm = -nrm; sp = -sp; }
                    // Нижняя грань оболочки: все точки — по ту же сторону, что и вершина (или на ней).
                    if (q.Any(x => Vector3d.Dot(nrm, x - q[i]) < -1e-9 * scale)) continue;
                    if (planes.Any(pl => (pl.Inward - nrm).Length < 1e-9 && Math.Abs(Vector3d.Dot(pl.Inward, pl.At - q[i])) < 1e-9 * scale)) continue;
                    planes.Add((q[i], nrm));
                }
        if (planes.Count == (c.Concave ? c.FaceNormals?.Length ?? 0 : 0)) return null;
        var mid = (p + q.Aggregate(Vector3d.Zero, (s, x) => s + x) / n) / 2;
        return FilletOp.HalfspaceCell(mid, p, scale, planes, warnings);
    }

    private static List<double> MergeClose(List<double> xs, double eps)
    {
        var res = new List<double>();
        foreach (var x in xs) if (res.Count == 0 || x - res[^1] > eps) res.Add(x);
        return res;
    }

    private static Vector3d Solve(double[,] m, Vector3d b)
    {
        var c0 = new Vector3d(m[0, 0], m[1, 0], m[2, 0]); var c1 = new Vector3d(m[0, 1], m[1, 1], m[2, 1]); var c2 = new Vector3d(m[0, 2], m[1, 2], m[2, 2]);
        double det = Vector3d.Dot(c0, Vector3d.Cross(c1, c2));
        if (Math.Abs(det) < 1e-15) return Vector3d.Zero;
        return new Vector3d(Vector3d.Dot(b, Vector3d.Cross(c1, c2)), Vector3d.Dot(c0, Vector3d.Cross(b, c2)), Vector3d.Dot(c0, Vector3d.Cross(c1, b))) / det;
    }
}
