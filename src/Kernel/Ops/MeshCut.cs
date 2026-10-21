using OpenTK.Mathematics;

namespace Parametric3D.Kernel.Ops;

/// <summary>
/// 21.10 (снимок пользователя 17:40): БЫСТРАЯ БУЛЕВА ПО СЕТКАМ — только для предпросмотра.
///
/// Точная булева обходного пути скругления (двоичное разбиение с точным переходником, проверками и сборкой колец) идёт
/// секунды; для вида детали, пока открыта панель, это лишнее. Здесь: многоугольники A, лежащие вдали от T, берутся как есть;
/// ближние куски A режутся плоскостями T, куски T — плоскостями ближней части A (дерево разбиения только для деления), и каждый
/// кусок относится к результату пробой точки по лучу против ВСЕЙ сетки другого тела (сетка по проекции — луч проверяет только
/// свою клетку). Классический csg.js на всей сетке тела (10 тыс. треугольников) шёл 140 с — дерево целого тела не строится.
/// Итог гранёный, со стыками кусков без согласования вершин: для показа и объёма по сетке годится, для дальнейших операций — нет.
/// </summary>
public static class MeshCut
{
    const double Eps = 1e-7;

    sealed class Node
    {
        public Vector3d N; public double W;
        public Node? Front, Back;
    }

    public enum Op { Subtract, Union, Intersect }

    /// <summary>A − T (subtract) или A ∪ T; null — вход пустой.</summary>
    public static List<CsgPolygon>? Run(List<CsgPolygon> a, List<CsgPolygon> t, bool subtract) => Run(a, t, subtract ? Op.Subtract : Op.Union);

    public static List<CsgPolygon>? Run(List<CsgPolygon> a, List<CsgPolygon> t, Op op)
    {
        if (a.Count == 0 || t.Count == 0) return null;
        var boxA = Box(a); var boxT = Box(t);
        double pad = 1e-6 * Math.Max(1, (boxA.Max - boxA.Min).Length);
        var aNear = new List<CsgPolygon>(); var res = new List<CsgPolygon>();
        foreach (var p in a) if (Overlaps(p, boxT, pad)) aNear.Add(p); else if (op != Op.Intersect) res.Add(p);   // вдали от T — снаружи T
        var tNear = t.Where(p => Overlaps(p, boxA, pad)).ToList();                             // вдали от A — снаружи A
        var gridA = new RayGrid(a); var gridT = new RayGrid(t);

        // куски A: вычитание и объединение — остаются, если сторона материала A у куска снаружи T; пересечение — если внутри.
        // Делят кусок только соседние (по рамке) многоугольники другого тела: дерево на каждый многоугольник из десятков.
        var hashT = new BoxHash(tNear); var hashA = new BoxHash(aNear);
        foreach (var p in aNear)
            foreach (var f in Split(Build(hashT.Near(p)), p))
            {
                var c = Centroid(f); double h = Probe(f);
                bool inT = gridT.Inside(c - f.N * h);
                if (op == Op.Intersect ? inT : !inT) res.Add(f);
            }
        // куски T: вычитание — остаются (вывернутыми), если снаружи T у куска материал A; объединение — если снаружи A;
        // пересечение — если внутри A
        foreach (var p in tNear)
            foreach (var f in Split(Build(hashA.Near(p)), p))
            {
                var c = Centroid(f); double h = Probe(f);
                if (op == Op.Subtract) { if (gridA.Inside(c + f.N * h)) { f.Flip(); res.Add(f); } }
                else
                {
                    bool inA = gridA.Inside(c - f.N * h);
                    if (op == Op.Intersect ? inA : !inA) res.Add(f);
                }
            }
        if (op == Op.Union) foreach (var p in t) if (!Overlaps(p, boxA, pad)) res.Add(p);
        return res;
    }

    /// <summary>Треугольники сетки показа тела, метка — грань (+ смещение); источники граней — в sources.</summary>
    public static List<CsgPolygon> MeshPolys(Topology.TSolid s, uint offset, Dictionary<uint, Surfaces.ISurface3?>? sources)
    {
        var md = Tess.Tessellator.Tessellate(s, facets: true);
        var surf = s.Faces.ToDictionary(f => f.Id, f => f.Surface);
        var res = new List<CsgPolygon>(md.Indices.Length / 3);
        for (int i = 0; i + 2 < md.Indices.Length; i += 3)
        {
            var a = md.Vertices[md.Indices[i]]; var b = md.Vertices[md.Indices[i + 1]]; var c = md.Vertices[md.Indices[i + 2]];
            var pa = new Vector3d(a.P.X, a.P.Y, a.P.Z); var pb = new Vector3d(b.P.X, b.P.Y, b.P.Z); var pc = new Vector3d(c.P.X, c.P.Y, c.P.Z);
            if (Vector3d.Cross(pb - pa, pc - pa).LengthSquared < 1e-20) continue;
            uint tag = a.FaceId + offset;
            if (sources != null && !sources.ContainsKey(tag)) sources[tag] = surf.TryGetValue(a.FaceId, out var sf) ? sf : null;
            res.Add(new CsgPolygon(new List<Vector3d> { pa, pb, pc }, tag));
        }
        return res;
    }

    /// <summary>
    /// 21.10: БУЛЕВА ПРЕДПРОСМОТРА ПО СЕТКАМ — тело из кусков (гранёное, без согласования вершин на стыках). null — не вышло.
    /// </summary>
    public static Topology.TSolid? Solid(Topology.TSolid a, Topology.TSolid b, Op op)
    {
        var sources = new Dictionary<uint, Surfaces.ISurface3?>();
        var pa = MeshPolys(a, 0, sources); var pb = MeshPolys(b, 1u << 30, sources);
        var r = Run(pa, pb, op);
        if (r == null || r.Count == 0) return null;
        Topology.TSolid solid;
        BooleanOp.PreviewMeshBuild = true;
        try { solid = BooleanOp.SolidFromPolygons(r, sources); }
        finally { BooleanOp.PreviewMeshBuild = false; }
        solid.TopologyConvention = Topology.TopologyConventions.Version;
        BooleanOp.MarkPreviewMesh(solid);
        return solid;
    }

    static double Probe(CsgPolygon f)
    {
        double s = 0; for (int i = 0; i < f.V.Count; i++) s = Math.Max(s, (f.V[i] - f.V[(i + 1) % f.V.Count]).Length);
        return Math.Max(1e-6, 1e-4 * s);
    }

    static Vector3d Centroid(CsgPolygon p)
    {
        var c = Vector3d.Zero; foreach (var v in p.V) c += v; return c / p.V.Count;
    }

    static (Vector3d Min, Vector3d Max) Box(List<CsgPolygon> ps)
    {
        var lo = new Vector3d(double.MaxValue); var hi = new Vector3d(double.MinValue);
        foreach (var p in ps) foreach (var v in p.V) { lo = Vector3d.ComponentMin(lo, v); hi = Vector3d.ComponentMax(hi, v); }
        return (lo, hi);
    }

    static bool Overlaps(CsgPolygon p, (Vector3d Min, Vector3d Max) b, double pad)
    {
        var lo = p.V[0]; var hi = p.V[0];
        foreach (var v in p.V) { lo = Vector3d.ComponentMin(lo, v); hi = Vector3d.ComponentMax(hi, v); }
        return lo.X <= b.Max.X + pad && hi.X >= b.Min.X - pad && lo.Y <= b.Max.Y + pad && hi.Y >= b.Min.Y - pad && lo.Z <= b.Max.Z + pad && hi.Z >= b.Min.Z - pad;
    }

    static Node? Build(List<CsgPolygon> ps)
    {
        if (ps.Count == 0) return null;
        // разделитель — середина списка (первый попавшийся на гладкой сетке даёт вырожденно глубокое дерево)
        var piv = ps[ps.Count / 2];
        var n = new Node { N = piv.N, W = piv.W };
        var fr = new List<CsgPolygon>(); var bk = new List<CsgPolygon>();
        foreach (var p in ps)
        {
            if (ReferenceEquals(p, piv)) continue;
            SplitBy(p, n.N, n.W, out var f, out var b, out bool cop);
            if (cop) continue;   // в плоскости узла — делить нечем
            if (f != null) fr.Add(f);
            if (b != null) bk.Add(b);
        }
        n.Front = Build(fr); n.Back = Build(bk);
        return n;
    }

    static IEnumerable<CsgPolygon> Split(Node? node, CsgPolygon p)
    {
        if (node == null) { yield return p; yield break; }
        SplitBy(p, node.N, node.W, out var f, out var b, out bool cop);
        if (cop)
        {
            foreach (var x in Split(node.Front, p)) foreach (var y in Split(node.Back, x)) yield return y;
            yield break;
        }
        if (f != null) foreach (var x in Split(node.Front, f)) yield return x;
        if (b != null) foreach (var x in Split(node.Back, b)) yield return x;
    }

    static void SplitBy(CsgPolygon p, Vector3d n, double w, out CsgPolygon? front, out CsgPolygon? back, out bool coplanar)
    {
        front = back = null; coplanar = false;
        int k = p.V.Count; var d = new double[k]; int fr = 0, bk = 0;
        for (int i = 0; i < k; i++) { d[i] = Vector3d.Dot(n, p.V[i]) - w; if (d[i] > Eps) fr++; else if (d[i] < -Eps) bk++; }
        if (fr == 0 && bk == 0) { coplanar = true; return; }
        if (bk == 0) { front = p; return; }
        if (fr == 0) { back = p; return; }
        var f = new List<Vector3d>(k + 1); var b = new List<Vector3d>(k + 1);
        for (int i = 0; i < k; i++)
        {
            int j = (i + 1) % k; var vi = p.V[i]; var vj = p.V[j];
            if (d[i] >= -Eps) f.Add(vi);
            if (d[i] <= Eps) b.Add(vi);
            if ((d[i] > Eps && d[j] < -Eps) || (d[i] < -Eps && d[j] > Eps))
            {
                double tt = d[i] / (d[i] - d[j]); var x = vi + (vj - vi) * tt;
                f.Add(x); b.Add(x);
            }
        }
        if (f.Count >= 3) front = p.Split(f);
        if (b.Count >= 3) back = p.Split(b);
    }

    /// <summary>Многоугольники по клеткам объёмной сетки (по рамке); соседи — пересекающие рамку запроса.</summary>
    sealed class BoxHash
    {
        readonly List<CsgPolygon> _p; readonly Dictionary<long, List<int>> _cells = new();
        readonly Vector3d _o; readonly double _h; readonly (Vector3d Lo, Vector3d Hi)[] _b;
        public BoxHash(List<CsgPolygon> ps)
        {
            _p = ps; _b = new (Vector3d, Vector3d)[ps.Count];
            var lo = new Vector3d(double.MaxValue); var hi = new Vector3d(double.MinValue); double sz = 0;
            for (int i = 0; i < ps.Count; i++)
            {
                var a = ps[i].V[0]; var b = a;
                foreach (var v in ps[i].V) { a = Vector3d.ComponentMin(a, v); b = Vector3d.ComponentMax(b, v); }
                _b[i] = (a, b); lo = Vector3d.ComponentMin(lo, a); hi = Vector3d.ComponentMax(hi, b); sz += (b - a).Length;
            }
            _o = ps.Count > 0 ? lo : Vector3d.Zero;
            _h = Math.Max(1e-9, ps.Count > 0 ? 2 * sz / ps.Count : 1);
            for (int i = 0; i < ps.Count; i++) foreach (long k in Keys(_b[i].Lo, _b[i].Hi)) { if (!_cells.TryGetValue(k, out var l)) _cells[k] = l = new List<int>(); l.Add(i); }
        }
        IEnumerable<long> Keys(Vector3d lo, Vector3d hi)
        {
            int x0 = (int)Math.Floor((lo.X - _o.X) / _h), x1 = (int)Math.Floor((hi.X - _o.X) / _h);
            int y0 = (int)Math.Floor((lo.Y - _o.Y) / _h), y1 = (int)Math.Floor((hi.Y - _o.Y) / _h);
            int z0 = (int)Math.Floor((lo.Z - _o.Z) / _h), z1 = (int)Math.Floor((hi.Z - _o.Z) / _h);
            if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1) > 4096) { x1 = x0 + 15; y1 = y0 + 15; z1 = z0 + 15; }
            for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++) for (int z = z0; z <= z1; z++)
                yield return ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);
        }
        public List<CsgPolygon> Near(CsgPolygon q)
        {
            var a = q.V[0]; var b = a;
            foreach (var v in q.V) { a = Vector3d.ComponentMin(a, v); b = Vector3d.ComponentMax(b, v); }
            double pad = 1e-6 * _h; a -= new Vector3d(pad); b += new Vector3d(pad);
            var seen = new HashSet<int>(); var res = new List<CsgPolygon>();
            foreach (long k in Keys(a, b))
                if (_cells.TryGetValue(k, out var l))
                    foreach (int i in l)
                    {
                        var (lo, hi) = _b[i];
                        if (lo.X > b.X || hi.X < a.X || lo.Y > b.Y || hi.Y < a.Y || lo.Z > b.Z || hi.Z < a.Z) continue;
                        if (seen.Add(i)) res.Add(_p[i]);
                    }
            return res;
        }
    }

    /// <summary>Луч вдоль +Z от точки; треугольники разложены по клеткам проекции на XY.</summary>
    sealed class RayGrid
    {
        readonly List<(Vector3d A, Vector3d B, Vector3d C)> _t = new();
        readonly List<int>[] _cells; readonly int _nx, _ny; readonly double _x0, _y0, _cx, _cy;
        public RayGrid(List<CsgPolygon> ps)
        {
            foreach (var p in ps) for (int i = 1; i + 1 < p.V.Count; i++) _t.Add((p.V[0], p.V[i], p.V[i + 1]));
            var lo = new Vector2d(double.MaxValue); var hi = new Vector2d(double.MinValue);
            foreach (var (a, b, c) in _t) foreach (var v in new[] { a, b, c }) { lo = Vector2d.ComponentMin(lo, v.Xy); hi = Vector2d.ComponentMax(hi, v.Xy); }
            int n = Math.Clamp((int)Math.Sqrt(_t.Count / 2.0), 1, 256);
            _nx = n; _ny = n; _x0 = lo.X; _y0 = lo.Y;
            _cx = Math.Max(1e-12, (hi.X - lo.X) / n); _cy = Math.Max(1e-12, (hi.Y - lo.Y) / n);
            _cells = new List<int>[n * n];
            for (int k = 0; k < _t.Count; k++)
            {
                var (a, b, c) = _t[k];
                int i0 = Cx(Math.Min(a.X, Math.Min(b.X, c.X))), i1 = Cx(Math.Max(a.X, Math.Max(b.X, c.X)));
                int j0 = Cy(Math.Min(a.Y, Math.Min(b.Y, c.Y))), j1 = Cy(Math.Max(a.Y, Math.Max(b.Y, c.Y)));
                for (int i = i0; i <= i1; i++) for (int j = j0; j <= j1; j++) (_cells[j * _nx + i] ??= new List<int>()).Add(k);
            }
        }
        int Cx(double x) => Math.Clamp((int)((x - _x0) / _cx), 0, _nx - 1);
        int Cy(double y) => Math.Clamp((int)((y - _y0) / _cy), 0, _ny - 1);

        public bool Inside(Vector3d p)
        {
            // три слегка разных точки (касание ребра луча — редкий случай), большинство голосов
            int votes = 0;
            for (int s = 0; s < 3; s++)
            {
                var q = p + new Vector3d(1e-7 * (s + 1) * 0.7071, 1e-7 * (s + 1) * 0.3137, 0);
                if (Odd(q)) votes++;
                if (votes >= 2 || votes + (2 - s) < 2) break;
            }
            return votes >= 2;
        }

        bool Odd(Vector3d p)
        {
            if (p.X < _x0 || p.Y < _y0 || p.X > _x0 + _cx * _nx || p.Y > _y0 + _cy * _ny) return false;
            var cell = _cells[Cy(p.Y) * _nx + Cx(p.X)]; if (cell == null) return false;
            int hits = 0;
            foreach (int k in cell)
            {
                var (a, b, c) = _t[k];
                // точка (p.x, p.y) внутри проекции треугольника (барицентрически), высота пересечения выше p.z
                double d = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
                if (Math.Abs(d) < 1e-300) continue;
                double l1 = ((b.Y - c.Y) * (p.X - c.X) + (c.X - b.X) * (p.Y - c.Y)) / d;
                double l2 = ((c.Y - a.Y) * (p.X - c.X) + (a.X - c.X) * (p.Y - c.Y)) / d;
                double l3 = 1 - l1 - l2;
                if (l1 < 0 || l2 < 0 || l3 < 0) continue;
                double z = l1 * a.Z + l2 * b.Z + l3 * c.Z;
                if (z > p.Z) hits++;
            }
            return (hits & 1) == 1;
        }
    }
}
