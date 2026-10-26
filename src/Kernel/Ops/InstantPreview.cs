using System.Runtime.CompilerServices;
using OpenTK.Mathematics;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Tess;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Ops;

/// <summary>
/// k113 (цель пользователя 21:47 — предпросмотр любой операции ≤ 20 мс): МГНОВЕННЫЙ ПРЕДПРОСМОТР ПОСЛЕДНЕЙ ОПЕРАЦИИ — ПО СЕТКАМ.
///
/// Замер (K01Checks previewbench, пластина K08 из 20 операций): предпросмотр бобышки шёл 225 мс, выреза 100 мс — почти всё
/// время сама булева (маршрут призмы собирает ВСЁ тело заново: 145 мс; разметка всех граней результата: 90 мс), хотя на экран
/// нужна только сетка. Здесь, когда операция — последняя в дереве и идёт предпросмотр (деталь потом пересчитывается точно):
/// сетка показа тела до операции (одна на сессию предпросмотра: многоугольники, нормали, рёбра, сетка лучей — готовы) режется
/// сеткой резца (MeshCut: вдали от резца — как есть, рядом — разбиение и проба лучом), и из кусков сразу собирается сетка
/// показа — без тела B-rep, без проверок, без истории. Рёбра: рёбра тела снаружи резца (внутри — при пересечении), рёбра резца
/// внутри тела (снаружи — при объединении), рёбра на поверхности другого тела, и линия пересечения поверхностей (пары
/// треугольников). Нормали — по исходным поверхностям граней. Тело в дереве — пустой носитель этой сетки: дальше по дереву он
/// не идёт (операция последняя), «ОК» и отпускание ручки считают точно, ошибки операции — у точного расчёта (второй круг).
/// Мутация P3D_INSTANTPREVIEW=0 — прежний предпросмотр.
/// </summary>
public static class InstantPreview
{
    public static readonly bool Off = Environment.GetEnvironmentVariable("P3D_INSTANTPREVIEW") == "0";

    /// <summary>Разрешено деревом: идёт предпросмотр и выполняется последняя операция (результат никто не использует).</summary>
    [ThreadStatic] public static bool Allowed;
    /// <summary>Тело на входе операции: по сеткам режется только оно (и носители, полученные из него), не промежуточные тела.</summary>
    [ThreadStatic] public static TSolid? Target;
    public static bool Active => !Off && Allowed;
    /// <summary>Булева над этим списком — итог операции (тело на входе или носитель), её можно резать по сеткам.</summary>
    public static bool Applies(List<TSolid> bodies) => Active && bodies.Count == 1 && (ReferenceEquals(bodies[0], Target) || IsCarrier(bodies[0]));
    public static long Hits, Declines;

    /// <summary>Сетка показа тела как многоугольники для реза (с нормалями вершин и рёбрами).</summary>
    sealed class Source
    {
        public List<CsgPolygon> Polys = new();
        public Dictionary<CsgPolygon, (Vector3 A, Vector3 B, Vector3 C)> Normals = new(ReferenceEqualityComparer.Instance);
        public Dictionary<uint, ISurface3?> Surfaces = new();
        public List<(uint Id, Vector3[] Pts)> Edges = new();
        public List<(uint Id, Vector3 P)> Points = new();
        MeshCut.Prepared? _prep;
        public MeshCut.Prepared Prep => _prep ??= new MeshCut.Prepared(Polys);
    }

    static readonly ConditionalWeakTable<TSolid, Source> _sources = new();
    sealed class CarrierData { public MeshData Mesh = null!; public Source Src = null!; public TSolid Body = null!; }
    static readonly ConditionalWeakTable<TSolid, CarrierData> _carriers = new();

    /// <summary>Сетка показа носителя мгновенного предпросмотра; null — обычное тело.</summary>
    public static MeshData? MeshOf(TSolid b) => _carriers.TryGetValue(b, out var c) ? c.Mesh : null;
    public static bool IsCarrier(TSolid b) => _carriers.TryGetValue(b, out _);
    /// <summary>Тело до операции, чьи номера граней, рёбер и вершин несёт сетка носителя (выбор в предпросмотре — по нему).</summary>
    public static TSolid? SourceBody(TSolid b) => _carriers.TryGetValue(b, out var c) ? c.Body : null;
    /// <summary>Тело для запросов формы (грани по имени, габарит): у носителя — тело до операции.</summary>
    public static TSolid Query(TSolid b) => SourceBody(b) ?? b;

    const uint ToolTag = 1u << 30;

    static Vector3d D(Vector3 v) => new(v.X, v.Y, v.Z);
    static Vector3 F(Vector3d v) => new((float)v.X, (float)v.Y, (float)v.Z);

    static Source FromMesh(MeshData md, IEnumerable<TFace> faces, uint offset)
    {
        var s = new Source();
        foreach (var f in faces) s.Surfaces[f.Id + offset] = f.Surface is FacetedSurface fs ? fs.Source : f.Surface;
        var ix = md.Indices; var vs = md.Vertices;
        s.Polys.Capacity = ix.Length / 3;
        for (int i = 0; i + 2 < ix.Length; i += 3)
        {
            var a = vs[ix[i]]; var b = vs[ix[i + 1]]; var c = vs[ix[i + 2]];
            var pa = D(a.P); var pb = D(b.P); var pc = D(c.P);
            if (Vector3d.Cross(pb - pa, pc - pa).LengthSquared < 1e-20) continue;
            var poly = new CsgPolygon(new List<Vector3d>(3) { pa, pb, pc }, a.FaceId + offset);
            s.Polys.Add(poly);
            s.Normals[poly] = (a.N, b.N, c.N);
        }
        foreach (var (id, pts) in md.EdgePolylines) s.Edges.Add((id, pts));
        foreach (var (id, p) in md.Points) s.Points.Add((id, p));
        return s;
    }

    static Source SourceOf(TSolid body)
    {
        if (_carriers.TryGetValue(body, out var c)) return c.Src;
        if (_sources.TryGetValue(body, out var s)) return s;
        MeshData md;
        using (Diagnostics.Prof.Time("мгновенный: сетка тела до операции")) md = Tessellator.Tessellate(body);
        s = FromMesh(md, body.Faces, 0);
        _sources.AddOrUpdate(body, s);
        return s;
    }

    /// <summary>Булева предпросмотра по сеткам; true — тело заменено носителем сетки.</summary>
    public static bool TryBoolean(List<TSolid> bodies, TSolid tool, BooleanOp.BooleanKind kind)
    {
        if (!Applies(bodies)) return false;
        using var _p = Diagnostics.Prof.Time("мгновенный предпросмотр");
        try
        {
            var src = SourceOf(bodies[0]);
            if (src.Polys.Count == 0) { Interlocked.Increment(ref Declines); return false; }
            MeshData tmd;
            using (Diagnostics.Prof.Time("мгновенный: сетка резца")) tmd = Tessellator.Tessellate(tool);
            var t = FromMesh(tmd, tool.Faces, ToolTag);
            if (t.Polys.Count == 0) { Interlocked.Increment(ref Declines); return false; }
            var op = kind switch { BooleanOp.BooleanKind.Subtract => MeshCut.Op.Subtract, BooleanOp.BooleanKind.Intersect => MeshCut.Op.Intersect, _ => MeshCut.Op.Union };
            // резец рядом с телом — разбиение; многоугольники резца тот рез переворачивает (вычитание) — нормали оттуда не берутся
            var toolPolys = t.Polys.Select(p => p.Clone()).ToList();
            List<CsgPolygon>? r;
            using (Diagnostics.Prof.Time("мгновенный: рез")) r = MeshCut.Run(src.Prep, toolPolys, op);
            if (r == null || r.Count == 0) { Interlocked.Increment(ref Declines); return false; }
            MeshData md; Source next;
            using (Diagnostics.Prof.Time("мгновенный: сетка показа")) (md, next) = Assemble(src, t, r, op);
            var carrier = new TSolid { TopologyConvention = TopologyConventions.Version };
            carrier.PendingAcceptance = () => "instant preview (mesh only)";
            carrier.SetProvenance(new Provenance { Operation = "InstantPreview" });
            BooleanOp.MarkPreviewMesh(carrier);
            _carriers.AddOrUpdate(carrier, new CarrierData { Mesh = md, Src = next, Body = SourceBody(bodies[0]) ?? bodies[0] });
            bodies[0] = carrier;
            Interlocked.Increment(ref Hits);
            return true;
        }
        catch (Exception ex) when (!KernelContext.IsCancellation(ex)) { Interlocked.Increment(ref Declines); return false; }
    }

    /// <summary>Сетка показа из кусков реза: нормали, грани подряд, рёбра.</summary>
    static (MeshData, Source) Assemble(Source a, Source t, List<CsgPolygon> r, MeshCut.Op op)
    {
        var next = new Source { Surfaces = new Dictionary<uint, ISurface3?>(a.Surfaces) };
        foreach (var kv in t.Surfaces) next.Surfaces[kv.Key] = kv.Value;
        // грани подряд (подсветка грани — по диапазону)
        var _pv = Diagnostics.Prof.Time("мгновенный: вершины");
        var order = r.OrderBy(p => p.Tag).ToList();
        int nv = 0, ni = 0; foreach (var p in order) { nv += p.V.Count; ni += 3 * (p.V.Count - 2); }
        var verts = new List<MeshVertex>(nv); var idx = new List<int>(ni);
        var md = new MeshData();
        var bounds = Bounds3.Empty;
        uint curTag = uint.MaxValue; int vStart = 0, iStart = 0;
        void Close(int vEnd, int iEnd)
        {
            if (curTag == uint.MaxValue || curTag >= ToolTag) return;
            md.FaceRanges[curTag] = new FaceRange { VertexStart = vStart, VertexCount = vEnd - vStart, IndexStart = iStart, IndexCount = iEnd - iStart };
        }
        foreach (var p in order)
        {
            if (p.Tag != curTag) { Close(verts.Count, idx.Count); curTag = p.Tag; vStart = verts.Count; iStart = idx.Count; }
            uint faceId = p.Tag >= ToolTag ? 0 : p.Tag;
            int b0 = verts.Count;
            if (p.V.Count == 3 && a.Normals.TryGetValue(p, out var nn))
            {
                // нетронутый треугольник тела — как был
                verts.Add(new MeshVertex { P = F(p.V[0]), N = nn.A, FaceId = faceId });
                verts.Add(new MeshVertex { P = F(p.V[1]), N = nn.B, FaceId = faceId });
                verts.Add(new MeshVertex { P = F(p.V[2]), N = nn.C, FaceId = faceId });
                idx.Add(b0); idx.Add(b0 + 1); idx.Add(b0 + 2);
                bounds.Add(p.V[0]); bounds.Add(p.V[1]); bounds.Add(p.V[2]);
                next.Polys.Add(p);
                continue;
            }
            else if (p.Origin is { V.Count: 3 } o && (a.Normals.TryGetValue(o, out var on) || t.Normals.TryGetValue(o, out on)))
            {
                // кусок треугольника сетки — нормали исходного треугольника (по барицентрическим координатам)
                var oa = o.V[0]; var ob = o.V[1]; var oc = o.V[2];
                var nrm = Vector3d.Cross(ob - oa, oc - oa); double area = nrm.LengthSquared;
                foreach (var v in p.V)
                {
                    Vector3d n = p.N;
                    if (area > 1e-30)
                    {
                        double wa = Vector3d.Dot(Vector3d.Cross(ob - v, oc - v), nrm) / area, wb = Vector3d.Dot(Vector3d.Cross(oc - v, oa - v), nrm) / area, wc = 1 - wa - wb;
                        var nb = D(on.A) * wa + D(on.B) * wb + D(on.C) * wc;
                        if (nb.LengthSquared > 1e-18) { nb.Normalize(); n = Vector3d.Dot(nb, p.N) >= 0 ? nb : -nb; }
                    }
                    verts.Add(new MeshVertex { P = F(v), N = F(n), FaceId = faceId });
                }
            }
            else
            {
                var surf = next.Surfaces.GetValueOrDefault(p.Tag);
                bool flat = surf == null || surf.IsPlanar;
                foreach (var v in p.V)
                {
                    var n = p.N;
                    if (!flat)
                        try
                        {
                            var sn = surf!.NormalAt(surf.ProjectToUV(v));
                            if (sn.LengthSquared > 1e-18) { sn.Normalize(); n = Vector3d.Dot(sn, p.N) >= 0 ? sn : -sn; }
                        }
                        catch { flat = true; }
                    verts.Add(new MeshVertex { P = F(v), N = F(n), FaceId = faceId });
                }
            }
            for (int k = 1; k + 1 < p.V.Count; k++) { idx.Add(b0); idx.Add(b0 + k); idx.Add(b0 + k + 1); }
            foreach (var v in p.V) bounds.Add(v);
            next.Polys.Add(p);
        }
        Close(verts.Count, idx.Count);
        md.Vertices = verts.ToArray(); md.Indices = idx.ToArray(); md.Bounds = bounds;
        _pv.Dispose();
        // нормали нетронутых треугольников тела переходят в следующий предпросмотр (цепочка булевых): те же объекты — та же таблица
        next.Normals = a.Normals;

        // ---- рёбра
        using (Diagnostics.Prof.Time("мгновенный: рёбра"))
        {
            var insideA = a.Prep; var insideT = MeshCut.InsideTest(t.Polys);
            double diag = Math.Max(1e-6, bounds.Diagonal);
            double eps = 1e-5 * diag;
            var tBox = Bounds3.Empty; foreach (var p in t.Polys) foreach (var v in p.V) tBox.Add(v);
            var tBoxE = tBox.Expanded(eps * 10);
            // точка на поверхности другого тела: пробы по сторонам дают разный ответ
            // на поверхности другого тела (ближе eps к его многоугольнику) — 0; иначе проба лучом
            var onT = MeshCut.OnSurfaceTest(t.Polys);
            int SideT(Vector3d m) => onT(m, eps) ? 0 : insideT(m) ? 1 : -1;
            int SideA(Vector3d m) => insideA.OnSurface(m, eps) ? 0 : insideA.Inside(m) ? 1 : -1;
            bool keepA(Vector3d m) { int s = SideT(m); return s == 0 || (op == MeshCut.Op.Intersect ? s > 0 : s < 0); }
            bool keepT(Vector3d m) { int s = SideA(m); return s == 0 || (op == MeshCut.Op.Union ? s < 0 : s > 0); }
            void Filter(List<(uint Id, Vector3[] Pts)> edges, Func<Vector3d, bool> keep, bool nearOnly, uint idOverride)
            {
                foreach (var (id, pts) in edges)
                {
                    var run = new List<Vector3>();
                    void Flush() { if (run.Count >= 2) { var e = (idOverride == uint.MaxValue ? id : idOverride, run.ToArray()); md.EdgePolylines.Add(e); next.Edges.Add(e); } run = new List<Vector3>(); }
                    for (int i = 1; i < pts.Length; i++)
                    {
                        Vector3d p0 = D(pts[i - 1]), p1 = D(pts[i]);
                        var sb = Bounds3.Empty; sb.Add(p0); sb.Add(p1);
                        bool near = !(sb.Min.X > tBoxE.Max.X || sb.Max.X < tBoxE.Min.X || sb.Min.Y > tBoxE.Max.Y || sb.Max.Y < tBoxE.Min.Y || sb.Min.Z > tBoxE.Max.Z || sb.Max.Z < tBoxE.Min.Z);
                        if (nearOnly && !near) { if (run.Count == 0) run.Add(pts[i - 1]); run.Add(pts[i]); continue; }
                        // длинный отрезок у резца — по кускам (вырез посреди длинной кромки)
                        int pieces = near ? Math.Clamp((int)Math.Ceiling((p1 - p0).Length / Math.Max(1e-9, 0.01 * diag)), 1, 64) : 1;
                        for (int k = 0; k < pieces; k++)
                        {
                            var q0 = p0 + (p1 - p0) * (k / (double)pieces); var q1 = p0 + (p1 - p0) * ((k + 1) / (double)pieces);
                            // кусок вне рамки резца — снаружи резца без пробы
                            bool inBox = nearOnly ? Overlap(q0, q1, tBoxE) : true;
                            if (!inBox || keep((q0 + q1) * 0.5)) { if (run.Count == 0) run.Add(F(q0)); run.Add(F(q1)); }
                            else Flush();
                        }
                    }
                    Flush();
                }
            }
            Filter(a.Edges, keepA, nearOnly: true, uint.MaxValue);
            Filter(t.Edges, keepT, nearOnly: false, 0);
            // линия пересечения поверхностей
            var seams = new List<(Vector3d, Vector3d)>();
            foreach (var q in t.Polys)
            {
                var qb = Bounds3.Empty; foreach (var v in q.V) qb.Add(v);
                if (qb.Min.X > a.Prep.Bounds.Max.X || qb.Max.X < a.Prep.Bounds.Min.X || qb.Min.Y > a.Prep.Bounds.Max.Y || qb.Max.Y < a.Prep.Bounds.Min.Y || qb.Min.Z > a.Prep.Bounds.Max.Z || qb.Max.Z < a.Prep.Bounds.Min.Z) continue;
                foreach (var p in a.Prep.Hash.Near(q))
                    if (Seam(p, q) is { } sg) seams.Add(sg);
            }
            foreach (var (s0, s1) in seams) { var e = (0u, new[] { F(s0), F(s1) }); md.EdgePolylines.Add(e); next.Edges.Add(e); }
            foreach (var (id, pt) in a.Points) if (keepA(D(pt))) { md.Points.Add((id, pt)); next.Points.Add((id, pt)); }
        }
        return (md, next);
    }

    static bool Overlap(Vector3d a, Vector3d b, Bounds3 box) =>
        !(Math.Min(a.X, b.X) > box.Max.X || Math.Max(a.X, b.X) < box.Min.X || Math.Min(a.Y, b.Y) > box.Max.Y || Math.Max(a.Y, b.Y) < box.Min.Y
          || Math.Min(a.Z, b.Z) > box.Max.Z || Math.Max(a.Z, b.Z) < box.Min.Z);

    /// <summary>Отрезок пересечения двух выпуклых плоских многоугольников (не в одной плоскости); null — не пересекаются.</summary>
    static (Vector3d, Vector3d)? Seam(CsgPolygon p, CsgPolygon q)
    {
        if (Math.Abs(Vector3d.Dot(p.N, q.N)) > 1 - 1e-9) return null;
        if (Cut(p, q.N, q.W) is not { } sp || Cut(q, p.N, p.W) is not { } sq) return null;
        var dir = Vector3d.Cross(p.N, q.N).Normalized();
        double a0 = Vector3d.Dot(sp.Item1, dir), a1 = Vector3d.Dot(sp.Item2, dir);
        double b0 = Vector3d.Dot(sq.Item1, dir), b1 = Vector3d.Dot(sq.Item2, dir);
        var pa = a0 <= a1 ? (sp.Item1, sp.Item2) : (sp.Item2, sp.Item1); if (a0 > a1) (a0, a1) = (a1, a0);
        var qa = b0 <= b1 ? (sq.Item1, sq.Item2) : (sq.Item2, sq.Item1); if (b0 > b1) (b0, b1) = (b1, b0);
        double lo = Math.Max(a0, b0), hi = Math.Min(a1, b1);
        if (hi - lo < 1e-9) return null;
        var start = a0 >= b0 ? pa.Item1 : qa.Item1;
        var end = a1 <= b1 ? pa.Item2 : qa.Item2;
        return (start, end);
    }

    /// <summary>Отрезок, по которому плоскость (n, w) режет выпуклый многоугольник; null — не режет.</summary>
    static (Vector3d, Vector3d)? Cut(CsgPolygon p, Vector3d n, double w)
    {
        const double e = 1e-9;
        Vector3d? x0 = null, x1 = null;
        int k = p.V.Count;
        for (int i = 0; i < k; i++)
        {
            var a = p.V[i]; var b = p.V[(i + 1) % k];
            double da = Vector3d.Dot(n, a) - w, db = Vector3d.Dot(n, b) - w;
            if ((da > e && db < -e) || (da < -e && db > e))
            {
                var x = a + (b - a) * (da / (da - db));
                if (x0 == null) x0 = x; else if (x1 == null) x1 = x;
            }
            else if (Math.Abs(da) <= e) { if (x0 == null) x0 = a; else if (x1 == null && (a - x0.Value).LengthSquared > 1e-24) x1 = a; }
        }
        return x0 is { } u && x1 is { } v && (u - v).LengthSquared > 1e-24 ? (u, v) : null;
    }
}
