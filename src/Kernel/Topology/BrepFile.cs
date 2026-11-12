using System.IO.Compression;
using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;

namespace Parametric3D.Kernel.Topology;

/// <summary>
/// k153: ЗАПИСЬ ПОСТРОЕННОГО ТЕЛА В ДОКУМЕНТ (как разделы тел Parasolid в файлах CAD). Документ хранит не только дерево
/// построения, но и его итог — B-rep: топологию (оболочки, грани, контуры, полурёбра, рёбра, вершины, их порядок), точную
/// геометрию (поверхности, кривые, p-кривые), допуски, имена сущностей (на них ссылаются операции) и прежние номера
/// граней, рёбер и вершин (по ним сетка показа из файла связывается с телом). Открытие файла восстанавливает тело без
/// перестроения дерева.
///
/// Геометрия по формуле (лента скругления, линия касания) записывается своей таблицей: точки между узлами — той же
/// вставкой, что у построенной (поверхность сечений), или кубической по плотной выборке с проверкой отхода (кривая по
/// формуле, ≤ 1e-7 мм). Многогранная точная форма (тела резца) — плоскости с целыми коэффициентами, многоугольники и
/// источники граней, без потерь. Чего формат ещё не умеет — исключение NotSupportedException: тело тогда не пишется, и файл
/// открывается перестроением, как прежде.
/// </summary>
public static class BrepFile
{
    const int Magic = 0x42443350;   // «P3DB»
    public const int FormatVersion = 4;   // k153: 2 — с многогранной точной формой; k156: 3 — фасеты по общим вершинам (2 читается)
    /// <summary>k156: мутация P3D_BREPINDEXED=0 — фасеты тройками точек (вид 2, файл больше).</summary>
    static readonly bool IndexedOff = Environment.GetEnvironmentVariable("P3D_BREPINDEXED") == "0";
    [ThreadStatic] static int _readVersion;
    /// <summary>Мутация P3D_BREPFILE=0: тело в документ не пишется и из него не читается (прежнее).</summary>
    public static readonly bool Off = Environment.GetEnvironmentVariable("P3D_BREPFILE") == "0";
    /// <summary>Разбор: тел записано / прочитано / не записано (тип геометрии не поддержан).</summary>
    public static long Written, Read, Unsupported;

    /// <summary>Одно прочитанное тело и соответствие прежних номеров сущностей новым.</summary>
    public sealed record Restored(TSolid Body, Dictionary<uint, uint> IdMap);

    // ================================================================ запись

    static readonly bool ThreadTablesEnv = Environment.GetEnvironmentVariable("P3D_BREPTHREAD") == "1";
    [ThreadStatic] static bool _threadTables;
    static bool ThreadTablesOn => ThreadTablesEnv || _threadTables;

    /// <summary>Write including tabulated thread rails (format round-trip checks; documents skip them).</summary>
    public static byte[] WriteWithThreadTables(IReadOnlyList<TSolid> bodies)
    {
        bool was = _threadTables; _threadTables = true;
        try { return Write(bodies); } finally { _threadTables = was; }
    }
    static readonly bool RailsOff = Environment.GetEnvironmentVariable("P3D_BREPRAILS") == "0";
    static bool Ruled(SectionSurface s) => !RailsOff && s.RuledData() != null && s.RailA!.Kind == "thread boundary" && s.RailB!.Kind == "thread boundary";

    /// <summary>
    /// Thread rail table: uniform samples, local degree-5 Lagrange interpolation (6 nearest nodes). A rail is a smooth
    /// helix-like curve (radius ~10, ω ~3.5 rad/mm): cubic Catmull–Rom needed up to 65537 points per rail for 1e-9 mm
    /// (a 50 mm NPT thread wrote ~540 MB); degree 5 reaches it with a few dozen.
    /// </summary>
    internal sealed class RailTable
    {
        readonly Vector3d[] _p; readonly double _t0, _t1;
        public RailTable(Vector3d[] p, double t0, double t1) { _p = p; _t0 = t0; _t1 = t1; }
        (int I0, double S) Locate(double t)
        {
            int n = _p.Length - 1; double span = _t1 - _t0;
            double s = span == 0 ? 0 : Math.Clamp((t - _t0) / span, 0, 1) * n;
            int m = Math.Min(5, n);
            int i0 = Math.Clamp((int)Math.Floor(s) - (m - 1) / 2, 0, n - m);
            return (i0, s);
        }
        public Vector3d Point(double t)
        {
            var (i0, s) = Locate(t); int m = Math.Min(5, _p.Length - 1);
            var r = Vector3d.Zero;
            for (int j = 0; j <= m; j++)
            {
                double l = 1;
                for (int k = 0; k <= m; k++) if (k != j) l *= (s - (i0 + k)) / (double)(j - k);
                r += _p[i0 + j] * l;
            }
            return r;
        }
        public Vector3d Deriv(double t)
        {
            var (i0, s) = Locate(t); int m = Math.Min(5, _p.Length - 1);
            int n = _p.Length - 1; double span = _t1 - _t0;
            if (n == 0 || span == 0) return Vector3d.Zero;
            var r = Vector3d.Zero;
            for (int j = 0; j <= m; j++)
            {
                double d = 0;
                for (int q = 0; q <= m; q++)
                {
                    if (q == j) continue;
                    double l = 1.0 / (j - q);
                    for (int k = 0; k <= m; k++) if (k != j && k != q) l *= (s - (i0 + k)) / (double)(j - k);
                    d += l;
                }
                r += _p[i0 + j] * d;
            }
            return r * (n / span);
        }
        /// <summary>Fewest uniform samples (doubling from 8) whose interpolation is within tol of the curve; null — none up to 65536.</summary>
        public static Vector3d[]? Sample(ProcCurve3 c, double tol)
        {
            double t0 = c.T0, t1 = c.T1;
            for (int n = 8; n <= 65536; n *= 2)
            {
                var pts = new Vector3d[n + 1];
                for (int i = 0; i <= n; i++) pts[i] = c.PointAt(t0 + (t1 - t0) * i / n);
                var rt = new RailTable(pts, t0, t1);
                double worst = 0;
                for (int i = 0; i < n && worst <= tol; i++)
                    foreach (double f in new[] { 0.25, 0.5, 0.75 })
                    {
                        double t = t0 + (t1 - t0) * (i + f) / n;
                        worst = Math.Max(worst, (rt.Point(t) - c.PointAt(t)).Length);
                    }
                if (worst <= tol) return pts;
            }
            return null;
        }
    }
    [ThreadStatic] static Dictionary<ICurve3, RailTable>? _readRails;
    /// <summary>Mutation P3D_BREPCOMP=0: the composition measure is not stored.</summary>
    static readonly bool CompositionOff = Environment.GetEnvironmentVariable("P3D_BREPCOMP") == "0";

    /// <summary>Largest stored body cache (compressed bytes).</summary>
    public const long MaxBytes = 64L << 20;

    public static byte[] Write(IReadOnlyList<TSolid> bodies)
    {
        var w = new Writer();
        try
        {
            foreach (var b in bodies) w.Scan(b);
            using var ms = new MemoryStream();
            using (var z = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            using (var bw = new BinaryWriter(z))
            {
                bw.Write(Magic); bw.Write(IndexedOff ? 2 : FormatVersion);
                w.Emit(bw, bodies);
            }
            // Any body cache this large is cheaper to rebuild than to store (and base64 in JSON has a hard limit).
            if (ms.Length > MaxBytes) throw new NotSupportedException($"body cache too large ({ms.Length / 1048576} MB), rebuilt on open");
            Interlocked.Add(ref Written, bodies.Count);
            return ms.ToArray();
        }
        catch (NotSupportedException) { Interlocked.Increment(ref Unsupported); throw; }
    }

    sealed class Writer
    {
        readonly Dictionary<ICurve3, int> _c3 = new(ReferenceEqualityComparer.Instance);
        readonly List<ICurve3> _c3l = new();
        readonly Dictionary<ISurface3, int> _s = new(ReferenceEqualityComparer.Instance);
        readonly List<ISurface3> _sl = new();
        readonly Dictionary<ICurve2, int> _c2 = new(ReferenceEqualityComparer.Instance);
        readonly List<ICurve2> _c2l = new();

        int C3(ICurve3 c)
        {
            if (_c3.TryGetValue(c, out int i)) return i;
            switch (c)
            {
                case IntersectionCurve3 ic: S(ic.A); S(ic.B); break;
                case SectionIsoCurve sc: S(sc.Surface); break;
                case ProcCurve3 pc when pc.ExactConic is { } ec: C3(ec); break;
                // Thread rails: compact degree-5 table (RailTable); P3D_BREPRAILS=0 — not cached (rebuilt on open).
                case ProcCurve3 pc when pc.Kind == "thread boundary" && RailsOff && !ThreadTablesOn:
                    throw new NotSupportedException("thread geometry is rebuilt on open");
            }
            _c3[c] = i = _c3l.Count; _c3l.Add(c);
            return i;
        }
        int S(ISurface3 s)
        {
            if (_s.TryGetValue(s, out int i)) return i;
            switch (s)
            {
                case ExtrudedSurface e: C3(e.Base); break;
                case RevolvedSurface r: C3(r.Base); break;
                case OffsetSurface o: S(o.Base); break;
                case TrimmedSurface t: S(t.Base); break;
                case FacetedSurface f when f.Source != null: S(f.Source); break;
                case SectionSurface ss when Ruled(ss): C3(ss.RailA!); C3(ss.RailB!); break;
            }
            _s[s] = i = _sl.Count; _sl.Add(s);
            return i;
        }
        int C2(ICurve2 c)
        {
            if (_c2.TryGetValue(c, out int i)) return i;
            _c2[c] = i = _c2l.Count; _c2l.Add(c);
            return i;
        }

        public void Scan(TSolid b)
        {
            // k153: многогранная точная форма (плоскости с целыми коэффициентами, многоугольники, источники граней) — пишется
            if (b.Exact != null && b.ExactAuthoritative) foreach (var src in b.Exact.Sources.Values) if (src != null) S(src);
            if (b.Attr != null) throw new NotSupportedException("solid attribute");
            foreach (var sh in b.Shells)
                foreach (var f in sh.Faces)
                {
                    if (f.Attr != null) throw new NotSupportedException("face attribute");
                    S(f.Surface);
                    foreach (var l in f.Loops)
                        foreach (var ce in l.CoEdges)
                        {
                            C3(ce.Edge.Curve);
                            if (ce.PCurve != null) C2(ce.PCurve);
                            if (ce.Edge.Attr != null || ce.Edge.V0.Attr != null || ce.Edge.V1.Attr != null) throw new NotSupportedException("edge or vertex attribute");
                        }
                }
        }

        public void Emit(BinaryWriter w, IReadOnlyList<TSolid> bodies)
        {
            // геометрия: сначала то, на что ссылаются (порядок добавления — ссылки раньше ссылающихся)
            // кривые и поверхности ссылаются друг на друга (ExtrudedSurface → кривая, IntersectionCurve3 → поверхности):
            // каждая пишется после того, на что ссылается
            w.Write(_c3l.Count); w.Write(_sl.Count); w.Write(_c2l.Count);
            var doneC = new bool[_c3l.Count]; var doneS = new bool[_sl.Count];
            void WC(int i) { if (doneC[i]) return; DepsC(_c3l[i]); w.Write((byte)1); w.Write(i); WriteCurve(w, _c3l[i]); doneC[i] = true; }
            void WS(int i) { if (doneS[i]) return; DepsS(_sl[i]); w.Write((byte)2); w.Write(i); WriteSurface(w, _sl[i]); doneS[i] = true; }
            void DepsC(ICurve3 c)
            {
                switch (c)
                {
                    case IntersectionCurve3 ic: WS(_s[ic.A]); WS(_s[ic.B]); break;
                    case SectionIsoCurve sc: WS(_s[sc.Surface]); break;
                    case ProcCurve3 pc when pc.ExactConic is { } ec: WC(_c3[ec]); break;
                }
            }
            void DepsS(ISurface3 s)
            {
                switch (s)
                {
                    case SectionSurface rs when Ruled(rs): WC(_c3[rs.RailA!]); WC(_c3[rs.RailB!]); break;
                    case ExtrudedSurface e: WC(_c3[e.Base]); break;
                    case RevolvedSurface r: WC(_c3[r.Base]); break;
                    case OffsetSurface o: WS(_s[o.Base]); break;
                    case TrimmedSurface t: WS(_s[t.Base]); break;
                    case FacetedSurface f when f.Source != null: WS(_s[f.Source]); break;
                }
            }
            for (int i = 0; i < _c3l.Count; i++) WC(i);
            for (int i = 0; i < _sl.Count; i++) WS(i);
            w.Write((byte)0);
            for (int i = 0; i < _c2l.Count; i++) WriteCurve2(w, _c2l[i]);

            w.Write(bodies.Count);
            foreach (var b in bodies) WriteSolid(w, b);
        }

        void WriteSolid(BinaryWriter w, TSolid b)
        {
            var verts = new List<TVertex>(); var vIdx = new Dictionary<TVertex, int>(ReferenceEqualityComparer.Instance);
            var edges = new List<TEdge>(); var eIdx = new Dictionary<TEdge, int>(ReferenceEqualityComparer.Instance);
            var coIdx = new Dictionary<TCoEdge, int>(ReferenceEqualityComparer.Instance);
            int co = 0;
            foreach (var sh in b.Shells) foreach (var f in sh.Faces) foreach (var l in f.Loops) foreach (var ce in l.CoEdges)
            {
                coIdx[ce] = co++;
                var e = ce.Edge;
                if (!eIdx.ContainsKey(e)) { eIdx[e] = edges.Count; edges.Add(e); }
                foreach (var v in new[] { e.V0, e.V1 }) if (!vIdx.ContainsKey(v)) { vIdx[v] = verts.Count; verts.Add(v); }
            }
            w.Write((int)b.Guarantee); w.Write(b.TopologyConvention); w.Write(b.Tag ?? "");
            w.Write(verts.Count);
            foreach (var v in verts) { V(w, v.P); w.Write(v.Tolerance); w.Write(v.Tag ?? ""); w.Write(v.Id); }
            w.Write(edges.Count);
            foreach (var e in edges)
            {
                w.Write(vIdx[e.V0]); w.Write(vIdx[e.V1]); w.Write(_c3[e.Curve]); w.Write(e.Tolerance); w.Write(e.Hidden); w.Write(e.Tag ?? ""); w.Write(e.Id);
            }
            w.Write(b.Shells.Count);
            foreach (var sh in b.Shells)
            {
                w.Write(sh.IsClosed); w.Write(sh.Tag ?? ""); w.Write(sh.Faces.Count);
                foreach (var f in sh.Faces)
                {
                    w.Write(_s[f.Surface]); w.Write(f.Reversed); w.Write(f.MaterialSlot); w.Write(f.SourceTag); w.Write(f.Tag ?? ""); w.Write(f.Id);
                    w.Write(f.InteriorHint.HasValue); if (f.InteriorHint is { } ih) V(w, ih);
                    w.Write(f.Loops.Count);
                    foreach (var l in f.Loops)
                    {
                        w.Write(l.IsOuter); w.Write(l.Tag ?? ""); w.Write(l.CoEdges.Count);
                        foreach (var ce in l.CoEdges)
                        {
                            w.Write(eIdx[ce.Edge]); w.Write(ce.Reversed);
                            w.Write(ce.PCurve != null ? _c2[ce.PCurve] : -1); w.Write(ce.PCurveDeviation);
                        }
                    }
                }
            }
            // порядок полурёбер у рёбер и рёбер у вершин — как у оригинала (FaceA/FaceB ребра — по месту полуребра)
            foreach (var e in edges)
            {
                var list = e.CoEdges.Where(coIdx.ContainsKey).ToList();
                w.Write(list.Count); foreach (var ce in list) w.Write(coIdx[ce]);
            }
            foreach (var v in verts)
            {
                var list = v.Edges.Where(eIdx.ContainsKey).ToList();
                w.Write(list.Count); foreach (var e in list) w.Write(eIdx[e]);
            }
            // v4: builder composition measure (thread bodies)
            if (!IndexedOff)
            {
                var cm = CompositionOff ? null : AnalyticVolume.Composition(b);
                w.Write(cm != null);
                if (cm is { } m) { w.Write(m.Volume); w.Write(m.Error); }
            }
            // k153: многогранная точная форма
            bool ex = b.Exact != null && b.ExactAuthoritative;
            w.Write(ex);
            if (!ex) return;
            var es = b.Exact!.Solid; var reg = es.Registry;
            var planeIds = new Dictionary<int, int>();
            int P(int id) { if (!planeIds.TryGetValue(id, out int k)) planeIds[id] = k = planeIds.Count; return k; }
            foreach (var poly in es.Polygons) { P(poly.Support.Id); foreach (var bp in poly.Bounds) P(bp.Id); }
            w.Write(planeIds.Count);
            foreach (var (id, _) in planeIds.OrderBy(kv => kv.Value))
            {
                var (a, bb, c, d) = reg.Coefficients(id);
                Big(w, a); Big(w, bb); Big(w, c); Big(w, d);
            }
            w.Write(reg.FaceHints.Count);
            foreach (var h in reg.FaceHints) { Big(w, h.A); Big(w, h.B); Big(w, h.C); Big(w, h.D); V(w, h.N); w.Write(h.W); }
            w.Write(es.Polygons.Count);
            foreach (var poly in es.Polygons)
            {
                w.Write(planeIds[poly.Support.Id]); w.Write(poly.Support.Side); w.Write(poly.Tag);
                w.Write(poly.Bounds.Length);
                foreach (var bp in poly.Bounds) { w.Write(planeIds[bp.Id]); w.Write(bp.Side); }
            }
            w.Write(b.Exact.Sources.Count);
            foreach (var (k, src) in b.Exact.Sources) { w.Write(k); w.Write(src != null ? _s[src] : -1); }
        }

        void WriteCurve(BinaryWriter w, ICurve3 c)
        {
            switch (c)
            {
                case LineCurve3 l: w.Write((byte)1); V(w, l.A); V(w, l.B); break;
                case ArcCurve3 a: w.Write((byte)2); F(w, a.Frame); w.Write(a.Radius); w.Write(a.A0); w.Write(a.A1); break;
                case EllipseCurve3 e: w.Write((byte)3); F(w, e.Frame); w.Write(e.RU); w.Write(e.RV); w.Write(e.A0); w.Write(e.A1); break;
                case BezierCurve3 bz: w.Write((byte)4); Vs(w, bz.P); break;
                case PolylineCurve3 pl: w.Write((byte)5); Vs(w, pl.Pts); break;
                case SplineCurve3 sp:
                {
                    var d = sp.Data; w.Write((byte)6); Vs(w, d.Cp); Ds(w, d.W); Ds(w, d.Knots); w.Write(d.Degree); break;
                }
                case IntersectionCurve3 ic: w.Write((byte)7); w.Write(_s[ic.A]); w.Write(_s[ic.B]); Vs(w, ic.Seed); break;
                case SectionIsoCurve sc:
                {
                    var (p0, p1) = sc.Ends;
                    w.Write((byte)8); w.Write(_s[sc.Surface]); w.Write(sc.V); w.Write(sc.U0); w.Write(sc.U1); V(w, p0); V(w, p1); Vs(w, sc.Seed); break;
                }
                case ProcCurve3 rc when rc.Kind == "thread boundary" && !RailsOff:
                {
                    var pts = RailTable.Sample(rc, 1e-9) ?? throw new NotSupportedException("thread rail does not tabulate within 1e-9 mm");
                    w.Write((byte)10); w.Write(rc.Kind); w.Write(rc.T0); w.Write(rc.T1); w.Write(rc.IsClosed);
                    w.Write(rc.ExactConic != null ? _c3[rc.ExactConic] : -1);
                    Vs(w, pts);
                    break;
                }
                case ProcCurve3 pc:
                {
                    var samples = SampleProc(pc) ?? throw new NotSupportedException("formula curve does not tabulate within 1e-7 mm");
                    w.Write((byte)9); w.Write(pc.Kind); w.Write(pc.T0); w.Write(pc.T1); w.Write(pc.IsClosed);
                    w.Write(pc.ExactConic != null ? _c3[pc.ExactConic] : -1);
                    Vs(w, samples);
                    break;
                }
                default: throw new NotSupportedException("curve " + c.GetType().Name);
            }
        }

        void WriteSurface(BinaryWriter w, ISurface3 s)
        {
            switch (s)
            {
                case PlaneSurface p: w.Write((byte)1); F(w, p.F); break;
                case CylinderSurface c: w.Write((byte)2); F(w, c.Axis); w.Write(c.Radius); break;
                case RevolvedSurface r: w.Write((byte)3); w.Write(_c3[r.Base]); F(w, r.Axis); w.Write(r.A0); w.Write(r.A1); break;
                case ExtrudedSurface e: w.Write((byte)4); w.Write(_c3[e.Base]); V(w, e.Dir); break;
                case NurbsSurface n:
                {
                    var d = n.Data; int nu = d.Cp.GetLength(0), nv = d.Cp.GetLength(1);
                    w.Write((byte)5); w.Write(nu); w.Write(nv);
                    for (int i = 0; i < nu; i++) for (int j = 0; j < nv; j++) { V(w, d.Cp[i, j]); w.Write(d.W[i, j]); }
                    Ds(w, d.Ku); Ds(w, d.Kv); w.Write(d.Du); w.Write(d.Dv);
                    break;
                }
                case FacetedSurface f:
                {
                    f.Facets.EnsureFilled();
                    w.Write((byte)6); w.Write(f.Source != null ? _s[f.Source] : -1); V(w, f.FallbackNormal); w.Write(f.FromLoops);
                    if (IndexedOff)
                    {
                        w.Write(f.Facets.Count);
                        foreach (var (a, b, c) in f.Facets) { V(w, a); V(w, b); V(w, c); }
                        break;
                    }
                    // k156: вершины фасетов — один раз (у сетки грани каждая вершина — в ~6 треугольниках), треугольники — номерами;
                    // точки те же до бита (словарь по точному значению)
                    var idx = new Dictionary<Vector3d, int>(); var verts = new List<Vector3d>();
                    int I(Vector3d q) { if (!idx.TryGetValue(q, out int k)) { k = verts.Count; idx[q] = k; verts.Add(q); } return k; }
                    var tri = new int[f.Facets.Count * 3]; int t = 0;
                    foreach (var (a, b, c) in f.Facets) { tri[t++] = I(a); tri[t++] = I(b); tri[t++] = I(c); }
                    w.Write(verts.Count); foreach (var q in verts) V(w, q);
                    w.Write(f.Facets.Count); foreach (int x in tri) w.Write(x);
                    break;
                }
                case OffsetSurface o: w.Write((byte)7); w.Write(_s[o.Base]); w.Write(o.Distance); break;
                case TrimmedSurface t:
                {
                    w.Write((byte)8); w.Write(_s[t.Base]); w.Write(t.Loops.Count);
                    foreach (var l in t.Loops) { w.Write(l.Count); foreach (var p in l) { w.Write(p.X); w.Write(p.Y); } }
                    break;
                }
                case SectionSurface rs when Ruled(rs):
                {
                    var (su, u0, u1, step) = rs.RuledData()!.Value;
                    w.Write((byte)10); w.Write(rs.Closed); w.Write(_c3[rs.RailA!]); w.Write(_c3[rs.RailB!]);
                    Ds(w, su); w.Write(u0); w.Write(u1); w.Write(step); Ds(w, rs.Breaks.ToArray());
                    break;
                }
                case SectionSurface ss:
                {
                    w.Write((byte)9); w.Write(ss.Closed); w.Write(ss.ParallelTable);
                    w.Write(ss.Sections.Count); foreach (var sec in ss.Sections) Sec(w, sec);
                    Ds(w, ss.Breaks.ToArray());
                    var pd = ss.ProcData();
                    w.Write(pd.HasValue);
                    if (pd is { } d)
                    {
                        Ds(w, d.StationU); w.Write(d.U0); w.Write(d.U1); w.Write(d.TableStep); Ds(w, d.Start);
                        w.Write(d.Pieces.Length);
                        foreach (var (h, secs) in d.Pieces) { w.Write(h); w.Write(secs.Length); foreach (var sec in secs) Sec(w, sec); }
                    }
                    break;
                }
                default: throw new NotSupportedException("surface " + s.GetType().Name);
            }
        }

        static void WriteCurve2(BinaryWriter w, ICurve2 c)
        {
            switch (c)
            {
                case Line2 l: w.Write((byte)1); V2(w, l.A); V2(w, l.B); break;
                case Arc2 a: w.Write((byte)2); V2(w, a.Centre); w.Write(a.Radius); w.Write(a.A0); w.Write(a.A1); break;
                case Conic2 k: w.Write((byte)3); V2(w, k.C); V2(w, k.P); V2(w, k.Q); w.Write(k.A0); w.Write(k.A1); break;
                case Polyline2 p: w.Write((byte)4); w.Write(p.Pts.Length); foreach (var q in p.Pts) V2(w, q); break;
                case Hermite2 h:
                    w.Write((byte)5); Ds(w, h.T);
                    foreach (var arr in new[] { h.P, h.Dl, h.Dr }) { w.Write(arr.Length); foreach (var q in arr) V2(w, q); }
                    break;
                default:
                {
                    // p-кривая по формуле (проекция) — плотной ломаной в тех же параметрах
                    const int n = 512;
                    w.Write((byte)6); w.Write(c.T0); w.Write(c.T1); w.Write(n + 1);
                    for (int i = 0; i <= n; i++) V2(w, c.PointAt(c.T0 + (c.T1 - c.T0) * i / n));
                    break;
                }
            }
        }
    }

    /// <summary>Выборка кривой по формуле: равномерно по параметру, вдвое гуще, пока кубическая вставка не сойдётся до 1e-7 мм (точность модели).</summary>
    static Vector3d[]? SampleProc(ProcCurve3 c)
    {
        if (SampleReuseOff) return SampleProcOld(c);
        // Adjacent run-out strips pinch to a point. Preserve their rail more accurately
        // than the general export tolerance so independent surface/curve tables stay sewn.
        double tolerance = c.Kind == "thread boundary" ? 1e-9 : 1e-7;
        // k156: середины, посчитанные для проверки уровня n, — это новые точки уровня 2n: каждая точка кривой считается один
        // раз (прежде — заново на каждом уровне; запись лофта с фаской — 18.5 с, из них 7.7 с — 76800 вычислений хребта).
        // Середины — все (без раннего выхода): они нужны следующему уровню. Мутация P3D_BREPSAMPLEREUSE=0.
        int n = 256;
        var pts = new Vector3d[n + 1];
        for (int i = 0; i <= n; i++) pts[i] = c.PointAt(c.T0 + (c.T1 - c.T0) * i / n);
        for (; n <= 65536; n *= 2)
        {
            var mids = new Vector3d[n];
            double worst = 0;
            for (int i = 0; i < n; i++)
            {
                mids[i] = c.PointAt(c.T0 + (c.T1 - c.T0) * (i + 0.5) / n);
                worst = Math.Max(worst, (CatmullRom(pts, (i + 0.5) / n) - mids[i]).Length);
            }
            if (worst <= tolerance) return pts;
            if (n * 2 > 65536) break;
            var next = new Vector3d[2 * n + 1];
            for (int i = 0; i < n; i++) { next[2 * i] = pts[i]; next[2 * i + 1] = mids[i]; }
            next[2 * n] = pts[n];
            pts = next;
        }
        return null;
    }

    static readonly bool SampleReuseOff = Environment.GetEnvironmentVariable("P3D_BREPSAMPLEREUSE") == "0";

    static Vector3d[]? SampleProcOld(ProcCurve3 c)
    {
        double tolerance = c.Kind == "thread boundary" ? 1e-9 : 1e-7;
        for (int n = 256; n <= 65536; n *= 2)
        {
            var pts = new Vector3d[n + 1];
            for (int i = 0; i <= n; i++) pts[i] = c.PointAt(c.T0 + (c.T1 - c.T0) * i / n);
            double worst = 0;
            for (int i = 0; i < n; i++)
            {
                double t = c.T0 + (c.T1 - c.T0) * (i + 0.5) / n;
                worst = Math.Max(worst, (CatmullRom(pts, (i + 0.5) / n) - c.PointAt(t)).Length);
                if (worst > tolerance) break;
            }
            if (worst <= tolerance) return pts;
        }
        return null;
    }

    /// <summary>Кубическая вставка Катмулла — Рома по равномерной выборке, x ∈ [0, 1].</summary>
    static Vector3d CatmullRom(Vector3d[] p, double x)
    {
        int n = p.Length - 1;
        double s = Math.Clamp(x, 0, 1) * n;
        int i = Math.Clamp((int)Math.Floor(s), 0, n - 1);
        double t = s - i;
        Vector3d P(int k) => k < 0 ? 2 * p[0] - p[1] : k > n ? 2 * p[n] - p[n - 1] : p[k];
        Vector3d p0 = P(i - 1), p1 = P(i), p2 = P(i + 1), p3 = P(i + 2);
        double t2 = t * t, t3 = t2 * t;
        return 0.5 * (2 * p1 + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 + (-p0 + 3 * p1 - 3 * p2 + p3) * t3);
    }

    // ================================================================ чтение

    public static List<Restored> ReadAll(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var z = new DeflateStream(ms, CompressionMode.Decompress);
        using var r = new BinaryReader(z);
        if (r.ReadInt32() != Magic) throw new InvalidDataException("not a body record");
        int ver = r.ReadInt32();
        if (ver != FormatVersion && ver != 3 && ver != 2) throw new InvalidDataException($"body record version {ver}");
        _readVersion = ver;
        _readRails = null;
        int nc = r.ReadInt32(), ns = r.ReadInt32(), n2 = r.ReadInt32();
        var c3 = new ICurve3[nc]; var ss = new ISurface3[ns];
        while (true)
        {
            byte k = r.ReadByte();
            if (k == 0) break;
            int i = r.ReadInt32();
            if (k == 1) c3[i] = ReadCurve(r, c3, ss); else ss[i] = ReadSurface(r, c3, ss);
        }
        var c2 = new ICurve2[n2];
        for (int i = 0; i < n2; i++) c2[i] = ReadCurve2(r);
        int nb = r.ReadInt32();
        var res = new List<Restored>(nb);
        for (int i = 0; i < nb; i++) res.Add(ReadSolid(r, c3, ss, c2));
        Interlocked.Add(ref Read, nb);
        return res;
    }

    static Restored ReadSolid(BinaryReader r, ICurve3[] c3, ISurface3[] ss, ICurve2[] c2)
    {
        var map = new Dictionary<uint, uint>();
        var b = new TSolid { Guarantee = (GuaranteeLevel)r.ReadInt32(), TopologyConvention = r.ReadInt32() };
        b.Tag = r.ReadString();
        var verts = new TVertex[r.ReadInt32()];
        for (int i = 0; i < verts.Length; i++)
        {
            var v = new TVertex(V(r)) { Tolerance = r.ReadDouble() };
            v.Tag = r.ReadString(); map[r.ReadUInt32()] = v.Id; verts[i] = v;
        }
        var edges = new TEdge[r.ReadInt32()];
        for (int i = 0; i < edges.Length; i++)
        {
            var e = new TEdge { V0 = verts[r.ReadInt32()], V1 = verts[r.ReadInt32()], Curve = c3[r.ReadInt32()], Tolerance = r.ReadDouble(), Hidden = r.ReadBoolean() };
            e.Tag = r.ReadString(); map[r.ReadUInt32()] = e.Id; edges[i] = e;
        }
        var coList = new List<TCoEdge>();
        int nsh = r.ReadInt32();
        for (int si = 0; si < nsh; si++)
        {
            var sh = new TShell { Solid = b, IsClosed = r.ReadBoolean() };
            sh.Tag = r.ReadString();
            b.Shells.Add(sh);
            int nf = r.ReadInt32();
            for (int fi = 0; fi < nf; fi++)
            {
                var f = new TFace { Surface = ss[r.ReadInt32()], Reversed = r.ReadBoolean(), MaterialSlot = r.ReadInt32(), SourceTag = r.ReadUInt32() };
                f.Tag = r.ReadString(); map[r.ReadUInt32()] = f.Id;
                if (r.ReadBoolean()) f.InteriorHint = V(r);
                sh.Add(f);
                int nl = r.ReadInt32();
                for (int li = 0; li < nl; li++)
                {
                    var l = new TLoop { Face = f, IsOuter = r.ReadBoolean() };
                    l.Tag = r.ReadString();
                    f.Loops.Add(l);
                    int nce = r.ReadInt32();
                    for (int ci = 0; ci < nce; ci++)
                    {
                        var e = edges[r.ReadInt32()]; bool rev = r.ReadBoolean(); int pc = r.ReadInt32(); double dev = r.ReadDouble();
                        var ce = new TCoEdge { Edge = e, Reversed = rev, Loop = l, PCurve = pc >= 0 ? c2[pc] : null, PCurveDeviation = dev };
                        l.CoEdges.Add(ce); coList.Add(ce);
                    }
                }
            }
        }
        foreach (var e in edges) { int n = r.ReadInt32(); for (int k = 0; k < n; k++) e.CoEdges.Add(coList[r.ReadInt32()]); }
        foreach (var v in verts) { int n = r.ReadInt32(); for (int k = 0; k < n; k++) v.Edges.Add(edges[r.ReadInt32()]); }
        (double V, double E)? comp = null;
        if (_readVersion >= 4 && r.ReadBoolean()) comp = (r.ReadDouble(), r.ReadDouble());
        // k153: многогранная точная форма (свой реестр плоскостей; канонический номер — по коэффициентам)
        if (r.ReadBoolean())
        {
            var reg = new Exact.PlaneRegistry();
            var planes = new Exact.OPlane[r.ReadInt32()];
            for (int i = 0; i < planes.Length; i++) planes[i] = reg.Plane(Big(r), Big(r), Big(r), Big(r));
            int nh = r.ReadInt32();
            for (int i = 0; i < nh; i++) reg.AddFaceHint(Big(r), Big(r), Big(r), Big(r), V(r), r.ReadDouble());
            // сторона записи — относительно канонической плоскости при записи; канонический вид плоскости тот же (НОД, знак),
            // поэтому сторона переносится как есть с поправкой на знак канонизации при чтении
            Exact.OPlane O(int k, int side) => new(planes[k].Id, side * planes[k].Side);
            var polys = new List<Exact.ExactPolygon>(r.ReadInt32());
            int np = polys.Capacity;
            for (int i = 0; i < np; i++)
            {
                int sk = r.ReadInt32(), sSide = r.ReadInt32(); uint tag = r.ReadUInt32();
                var bounds = new Exact.OPlane[r.ReadInt32()];
                for (int j = 0; j < bounds.Length; j++) { int bk = r.ReadInt32(), bs = r.ReadInt32(); bounds[j] = O(bk, bs); }
                polys.Add(new Exact.ExactPolygon(O(sk, sSide), bounds, tag));
            }
            var sources = new Dictionary<uint, ISurface3?>();
            int nsrc = r.ReadInt32();
            for (int i = 0; i < nsrc; i++) { uint k = r.ReadUInt32(); int si = r.ReadInt32(); sources[k] = si >= 0 ? ss[si] : null; }
            b.Exact = new Exact.ExactBody(new Exact.ExactSolid(reg, polys), sources);
        }
        if (comp is { } cm) AnalyticVolume.RememberComposition(b, cm.V, cm.E);
        return new Restored(b, map);
    }

    static ICurve3 ReadCurve(BinaryReader r, ICurve3[] c3, ISurface3[] ss)
    {
        byte k = r.ReadByte();
        switch (k)
        {
            case 1: return new LineCurve3(V(r), V(r));
            case 2: { var f = F(r); return new ArcCurve3(f, r.ReadDouble(), r.ReadDouble(), r.ReadDouble()); }
            case 3: { var f = F(r); return new EllipseCurve3(f, r.ReadDouble(), r.ReadDouble(), r.ReadDouble(), r.ReadDouble()); }
            case 4: return new BezierCurve3(Vs(r));
            case 5: return new PolylineCurve3(Vs(r));
            case 6: { var cp = Vs(r); var wt = Ds(r); var kn = Ds(r); int deg = r.ReadInt32(); return new SplineCurve3(cp, deg, wt, kn); }
            case 7: { var a = ss[r.ReadInt32()]; var b = ss[r.ReadInt32()]; return new IntersectionCurve3(a, b, Vs(r)); }
            case 8:
            {
                var s = (SectionSurface)ss[r.ReadInt32()]; double v = r.ReadDouble(), u0 = r.ReadDouble(), u1 = r.ReadDouble();
                var p0 = V(r); var p1 = V(r); var seed = Vs(r);
                return new SectionIsoCurve(s, v, u0, u1, p0, p1, seed);
            }
            case 9:
            {
                string kind = r.ReadString(); double t0 = r.ReadDouble(), t1 = r.ReadDouble(); bool closed = r.ReadBoolean();
                int conic = r.ReadInt32(); var pts = Vs(r);
                double span = t1 - t0;
                var pc = new ProcCurve3(t => CatmullRom(pts, span == 0 ? 0 : (t - t0) / span), t0, t1, closed, kind);
                if (conic >= 0) pc.ExactConic = (EllipseCurve3)c3[conic];
                return pc;
            }
            case 10:
            {
                string kind = r.ReadString(); double t0 = r.ReadDouble(), t1 = r.ReadDouble(); bool closed = r.ReadBoolean();
                int conic = r.ReadInt32(); var pts = Vs(r);
                if (pts.Length < 1) throw new InvalidDataException("empty rail table");
                var rt = new RailTable(pts, t0, t1);
                var pc = new ProcCurve3(rt.Point, t0, t1, closed, kind);
                if (conic >= 0) pc.ExactConic = (EllipseCurve3)c3[conic];
                (_readRails ??= new(ReferenceEqualityComparer.Instance))[pc] = rt;
                return pc;
            }
            default: throw new InvalidDataException("curve kind " + k);
        }
    }

    static ISurface3 ReadSurface(BinaryReader r, ICurve3[] c3, ISurface3[] ss)
    {
        byte k = r.ReadByte();
        switch (k)
        {
            case 1: return new PlaneSurface(F(r));
            case 2: { var f = F(r); return new CylinderSurface(f, r.ReadDouble()); }
            case 3: { var c = c3[r.ReadInt32()]; var f = F(r); return new RevolvedSurface(c, f, r.ReadDouble(), r.ReadDouble()); }
            case 4: { var c = c3[r.ReadInt32()]; return new ExtrudedSurface(c, V(r)); }
            case 5:
            {
                int nu = r.ReadInt32(), nv = r.ReadInt32();
                var cp = new Vector3d[nu, nv]; var wt = new double[nu, nv];
                for (int i = 0; i < nu; i++) for (int j = 0; j < nv; j++) { cp[i, j] = V(r); wt[i, j] = r.ReadDouble(); }
                var ku = Ds(r); var kv = Ds(r); int du = r.ReadInt32(), dv = r.ReadInt32();
                return new NurbsSurface(cp, du, dv, wt, ku, kv);
            }
            case 6:
            {
                int src = r.ReadInt32(); var fn = V(r); bool fromLoops = r.ReadBoolean();
                var f = new FacetedSurface(src >= 0 ? ss[src] : null, fn) { FromLoops = fromLoops };
                if (_readVersion >= 3)
                {
                    int nv = r.ReadInt32(); var vs = new Vector3d[nv];
                    for (int i = 0; i < nv; i++) vs[i] = V(r);
                    int nt = r.ReadInt32();
                    var tri3 = new List<(Vector3d, Vector3d, Vector3d)>(nt);
                    for (int i = 0; i < nt; i++) tri3.Add((vs[r.ReadInt32()], vs[r.ReadInt32()], vs[r.ReadInt32()]));
                    f.Facets.AddRange(tri3);
                    return f;
                }
                int n = r.ReadInt32();
                var tris = new List<(Vector3d, Vector3d, Vector3d)>(n);
                for (int i = 0; i < n; i++) tris.Add((V(r), V(r), V(r)));
                f.Facets.AddRange(tris);
                return f;
            }
            case 7: { var b = ss[r.ReadInt32()]; return new OffsetSurface(b, r.ReadDouble()); }
            case 8:
            {
                var b = ss[r.ReadInt32()]; int nl = r.ReadInt32();
                var loops = new List<List<Vector2d>>(nl);
                for (int i = 0; i < nl; i++) { int n = r.ReadInt32(); var l = new List<Vector2d>(n); for (int j = 0; j < n; j++) l.Add(new Vector2d(r.ReadDouble(), r.ReadDouble())); loops.Add(l); }
                return new TrimmedSurface(b, loops);
            }
            case 9:
            {
                bool closed = r.ReadBoolean(), par = r.ReadBoolean();
                var secs = new SweepSection[r.ReadInt32()];
                for (int i = 0; i < secs.Length; i++) secs[i] = Sec(r);
                var breaks = Ds(r);
                if (!r.ReadBoolean()) return new SectionSurface(secs, closed);
                var su = Ds(r); double u0 = r.ReadDouble(), u1 = r.ReadDouble(), step = r.ReadDouble(); var start = Ds(r);
                var pieces = new (double, SweepSection[])[r.ReadInt32()];
                for (int i = 0; i < pieces.Length; i++)
                {
                    double h = r.ReadDouble(); var arr = new SweepSection[r.ReadInt32()];
                    for (int j = 0; j < arr.Length; j++) arr[j] = Sec(r);
                    pieces[i] = (h, arr);
                }
                var s = SectionSurface.FromTable(su, secs, closed, u0, u1, breaks, step, start, pieces);
                return par ? WithParallel(s) : s;
            }
            case 10:
            {
                bool closed = r.ReadBoolean();
                var a = (ProcCurve3)c3[r.ReadInt32()]; var b = (ProcCurve3)c3[r.ReadInt32()];
                var su = Ds(r); double u0 = r.ReadDouble(), u1 = r.ReadDouble(), step = r.ReadDouble(); var breaks = Ds(r);
                if (su.Length < 2) throw new InvalidDataException("ruled surface stations");
                Func<double, Vector3d> D(ProcCurve3 c) => _readRails != null && _readRails.TryGetValue(c, out var t) ? t.Deriv
                    : u => { double h = 1e-6 * Math.Max(1e-9, Math.Abs(c.T1 - c.T0)); return (c.PointAt(u + h) - c.PointAt(u - h)) / (2 * h); };
                return SectionSurface.FromRails(a, b, D(a), D(b), su, closed, u0, u1, breaks, step);
            }
            default: throw new InvalidDataException("surface kind " + k);
        }
    }
    static SectionSurface WithParallel(SectionSurface s) => s;   // таблица уже есть — параллельный расчёт не нужен

    static ICurve2 ReadCurve2(BinaryReader r)
    {
        byte k = r.ReadByte();
        switch (k)
        {
            case 1: return new Line2(V2(r), V2(r));
            case 2: { var c = V2(r); return new Arc2(c, r.ReadDouble(), r.ReadDouble(), r.ReadDouble()); }
            case 3: { var c = V2(r); var p = V2(r); var q = V2(r); return new Conic2(c, p, q, r.ReadDouble(), r.ReadDouble()); }
            case 4: { int n = r.ReadInt32(); var a = new Vector2d[n]; for (int i = 0; i < n; i++) a[i] = V2(r); return new Polyline2(a); }
            case 5:
            {
                var t = Ds(r);
                Vector2d[] A() { int n = r.ReadInt32(); var a = new Vector2d[n]; for (int i = 0; i < n; i++) a[i] = V2(r); return a; }
                var p = A(); var dl = A(); var dr = A();
                return new Hermite2(t, p, dl, dr);
            }
            case 6:
            {
                double t0 = r.ReadDouble(), t1 = r.ReadDouble(); int n = r.ReadInt32();
                var a = new Vector2d[n]; for (int i = 0; i < n; i++) a[i] = V2(r);
                return new SampledCurve2(t0, t1, a);
            }
            default: throw new InvalidDataException("pcurve kind " + k);
        }
    }

    /// <summary>p-кривая по формуле из документа: ломаная по равномерной выборке в своём промежутке параметра.</summary>
    sealed class SampledCurve2(double t0, double t1, Vector2d[] pts) : ICurve2
    {
        public double T0 => t0;
        public double T1 => t1;
        public bool IsExact => false;
        public Vector2d PointAt(double t)
        {
            int n = pts.Length - 1;
            double x = t1 == t0 ? 0 : Math.Clamp((t - t0) / (t1 - t0), 0, 1) * n;
            int i = Math.Clamp((int)Math.Floor(x), 0, n - 1);
            return pts[i] + (pts[i + 1] - pts[i]) * (x - i);
        }
    }

    // ================================================================ простые записи

    static void V(BinaryWriter w, Vector3d v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
    static Vector3d V(BinaryReader r) => new(r.ReadDouble(), r.ReadDouble(), r.ReadDouble());
    static void V2(BinaryWriter w, Vector2d v) { w.Write(v.X); w.Write(v.Y); }
    static Vector2d V2(BinaryReader r) => new(r.ReadDouble(), r.ReadDouble());
    static void Vs(BinaryWriter w, IReadOnlyList<Vector3d> a) { w.Write(a.Count); foreach (var v in a) V(w, v); }
    static Vector3d[] Vs(BinaryReader r) { int n = r.ReadInt32(); var a = new Vector3d[n]; for (int i = 0; i < n; i++) a[i] = V(r); return a; }
    static void Ds(BinaryWriter w, IReadOnlyList<double> a) { w.Write(a.Count); foreach (var d in a) w.Write(d); }
    static double[] Ds(BinaryReader r) { int n = r.ReadInt32(); var a = new double[n]; for (int i = 0; i < n; i++) a[i] = r.ReadDouble(); return a; }
    static void Big(BinaryWriter w, System.Numerics.BigInteger v) { var a = v.ToByteArray(); w.Write(a.Length); w.Write(a); }
    static System.Numerics.BigInteger Big(BinaryReader r) => new(r.ReadBytes(r.ReadInt32()));
    static void F(BinaryWriter w, Frame3 f) { V(w, f.Origin); V(w, f.U); V(w, f.V); V(w, f.W); }
    static Frame3 F(BinaryReader r) => Frame3.Raw(V(r), V(r), V(r), V(r));
    static void Sec(BinaryWriter w, SweepSection s)
    {
        w.Write(s.IsArc); V(w, s.A); V(w, s.B); V(w, s.C); V(w, s.E1); V(w, s.E2); w.Write(s.R); w.Write(s.Phi);
        w.Write(s.Q != null); if (s.Q != null) Vs(w, s.Q);
    }
    static SweepSection Sec(BinaryReader r)
    {
        bool arc = r.ReadBoolean(); var a = V(r); var b = V(r); var c = V(r); var e1 = V(r); var e2 = V(r); double rr = r.ReadDouble(), phi = r.ReadDouble();
        var q = r.ReadBoolean() ? Vs(r) : null;
        return SweepSection.Raw(arc, a, b, c, e1, e2, rr, phi, q);
    }
}
