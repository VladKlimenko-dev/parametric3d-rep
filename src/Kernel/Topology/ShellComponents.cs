using OpenTK.Mathematics;

namespace Parametric3D.Kernel.Topology;

/// <summary>
/// KRN-03: ОБОЛОЧКИ, ПОЛОСТИ И ОТДЕЛЬНЫЕ ТЕЛА.
///
/// Сборка результата клала все связные компоненты границы (тело и полость, два раздельных куска) в одну TShell одного тела
/// (замер: объединение раздельных брусков, разрез бруска пазом — одно тело с двумя компонентами; полость — одна оболочка
/// с двумя компонентами). Инструкция KRN-03: «несколько раздельных тел не смешивать с несколькими полостями одного тела».
///
/// Split: компоненты — грани, связанные общими рёбрами; знаковый объём компоненты (по нормалям граней) > 0 — наружная
/// оболочка, &lt; 0 — полость. Полость принадлежит наименьшему наружному телу, которое её содержит (точный классификатор,
/// иначе — чётность луча по фасетам). Каждая наружная оболочка со своими полостями — отдельное тело. Точная многогранная
/// форма (истина тела) делится по связности вершин реестра; если части однозначно не сопоставляются — снимается с
/// предупреждением (тело остаётся B-rep), а не делится наугад.
/// </summary>
public static class ShellComponents
{
    /// <summary>Разбор: последнее разделение (компоненты, тела, полости, судьба точной формы).</summary>
    [ThreadStatic] public static Action<string>? Debug;

    public sealed class Outcome
    {
        public readonly List<TSolid> Bodies = new();
        public int Components, Cavities, DroppedCrumbs;
        public string? Warning;
    }

    static readonly bool DropTinyCavityOff = Environment.GetEnvironmentVariable("P3D_DROPTINYCAVITY") == "0";
    public static long TinyCavitiesDropped;

    public static Outcome Split(TSolid solid)
    {
        var res = new Outcome();
        // нормализация — ДО сбора граней: она может делить многообластные грани (иначе компоненты — по устаревшему списку)
        if (solid.TopologyConvention < TopologyConventions.Version) TopologyConventions.Normalize(solid);
        var faces = solid.Faces.ToList();
        if (faces.Count == 0) { res.Bodies.Add(solid); return res; }

        // 1. компоненты по общим рёбрам
        var index = new Dictionary<TFace, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < faces.Count; i++) index[faces[i]] = i;
        var parent = Enumerable.Range(0, faces.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        foreach (var f in faces)
            foreach (var e in f.Edges)
                foreach (var ce in e.CoEdges)
                    if (ce.Loop?.Face is { } g && index.TryGetValue(g, out int j)) { int a = Find(index[f]), b = Find(j); if (a != b) parent[a] = b; }
        var comps = faces.GroupBy(f => Find(index[f])).Select(g => g.ToList()).ToList();
        res.Components = comps.Count;
        Debug?.Invoke($"enter: body {solid.Id} faces {faces.Count} components {comps.Count} exact {(solid.Exact == null ? "-" : solid.ExactAuthoritative ? "auth" : "derived")} published {solid.IsPublished}");
        if (comps.Count == 1) { res.Bodies.Add(solid); return res; }

        // 2. знаковые объёмы
        var vols = comps.Select(SignedVolume).ToList();
        var outers = Enumerable.Range(0, comps.Count).Where(i => vols[i] > 0).ToList();
        var inners = Enumerable.Range(0, comps.Count).Where(i => vols[i] < 0).ToList();
        if (outers.Count == 0) { res.Warning = "no outer shell among components"; res.Bodies.Add(solid); return res; }

        // 3. тела из наружных оболочек
        var bodyOf = new Dictionary<int, TSolid>();
        foreach (int i in outers)
        {
            var nb = NewBody(solid);
            var sh = nb.AddShell();
            foreach (var f in comps[i]) sh.Add(f);
            bodyOf[i] = nb;
        }
        // 4. полости — наименьшему содержащему телу
        foreach (int k in inners)
        {
            int owner = -1; double best = double.MaxValue;
            // пробы — вершины полости; у полости без вершин (целая сфера) — точки её поверхности (вершины фасетов)
            var probe = comps[k].SelectMany(f => f.Edges).SelectMany(e => new[] { e.V0.P, e.V1.P }).Take(3).ToList();
            if (probe.Count == 0) probe = Triangles(comps[k]).Take(3).Select(t => (t.A + t.B + t.C) / 3).ToList();
            foreach (int i in outers.OrderBy(i => vols[i]))
            {
                if (vols[i] >= best) continue;
                if (Contains(bodyOf[i], comps[i], probe)) { owner = i; best = vols[i]; }
            }
            // k140 (корпус лофтов): вывернутая крошка (объём меньше 10⁻⁵ тела) вне всех оболочек — след разбиения у острого
            // угла после фаски: отбрасывается (её грани в тело не идут). Мутация P3D_DROPTINYCAVITY=0.
            if (owner < 0 && !DropTinyCavityOff && Math.Abs(vols[k]) < 1e-5 * outers.Sum(i => vols[i]))
            { res.DroppedCrumbs++; Interlocked.Increment(ref TinyCavitiesDropped); continue; }
            if (owner < 0) { res.Warning = $"a cavity (volume {vols[k]:G6}) lies in no outer shell"; Restore(solid, comps); res.Bodies.Clear(); res.Bodies.Add(solid); return res; }
            var sh = bodyOf[owner].AddShell();
            foreach (var f in comps[k]) sh.Add(f);
            res.Cavities++;
        }
        res.Bodies.AddRange(outers.Select(i => bodyOf[i]));

        // 5. точная форма
        if (solid.Exact != null && solid.ExactAuthoritative)
        {
            if (!SplitExact(solid, res.Bodies, out var why)) res.Warning = "the exact polyhedral form was not split: " + why;
        }
        Debug?.Invoke($"split: components {comps.Count}, bodies {res.Bodies.Count}, cavities {res.Cavities}, volumes [{string.Join(", ", vols.Select(v => v.ToString("G6")))}]{(res.Warning != null ? ", " + res.Warning : "")}");
        return res;
    }

    /// <summary>
    /// KRN-03. ИНВАРИАНТ ОБОЛОЧЕК ТЕЛА (03.5, 03.6): каждая оболочка — одна связная компонента граней (связь — общие рёбра
    /// в этой оболочке); первая оболочка — наружная (объём > 0), прочие — полости (объём &lt; 0), лежащие внутри наружной.
    /// null — выполнен; иначе — что нарушено. Проверка по B-rep (знаковый объём — точные интегралы граней, иначе фасеты
    /// граней; вложенность — классификатор, иначе луч по граням наружной оболочки).
    /// </summary>
    public static string? Invariant(TSolid solid)
    {
        var shells = solid.Shells.Where(sh => sh.Faces.Count > 0).ToList();
        if (shells.Count == 0) return null;
        for (int k = 0; k < shells.Count; k++)
        {
            var faces = shells[k].Faces.ToList();
            var index = new Dictionary<TFace, int>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < faces.Count; i++) index[faces[i]] = i;
            var parent = Enumerable.Range(0, faces.Count).ToArray();
            int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
            foreach (var f in faces)
            {
                if (!ReferenceEquals(f.Shell, shells[k])) return $"shell {k}: a face belongs to another shell";
                foreach (var e in f.Edges)
                    foreach (var ce in e.CoEdges)
                        if (ce.Loop?.Face is { } g && index.TryGetValue(g, out int j)) { int a = Find(index[f]), b = Find(j); if (a != b) parent[a] = b; }
            }
            int comps = faces.Select(f => Find(index[f])).Distinct().Count();
            if (comps != 1) return $"shell {k}: {comps} connected components in one shell";
        }
        // одна оболочка: её знак проверяет сетка (вывернутое тело) — объём не считается на каждой операции
        if (shells.Count == 1) return null;
        var vols = shells.Select(sh => SignedVolume(sh.Faces.ToList())).ToList();
        if (!(vols[0] > 0)) return $"shell 0 is not outer (signed volume {vols[0]:G6})";
        for (int k = 1; k < shells.Count; k++)
        {
            if (!(vols[k] < 0)) return $"shell {k} is not a cavity (signed volume {vols[k]:G6})";
            var probe = shells[k].Faces.SelectMany(f => f.Edges).SelectMany(e => new[] { e.V0.P, e.V1.P }).Take(3).ToList();
            if (probe.Count == 0) probe = Triangles(shells[k].Faces.ToList()).Take(3).Select(t => (t.A + t.B + t.C) / 3).ToList();
            if (!Contains(solid, shells[0].Faces.ToList(), probe)) return $"cavity shell {k} does not lie inside the outer shell";
        }
        return null;
    }

    static TSolid NewBody(TSolid src)
    {
        var nb = new TSolid { Guarantee = src.Guarantee, TopologyConvention = src.TopologyConvention, Attr = src.Attr };
        nb.Tag = src.Tag;
        return nb;
    }

    /// <summary>Отказ после частичного переноса: грани возвращаются в исходное тело (одна оболочка, как было).</summary>
    static void Restore(TSolid solid, List<List<TFace>> comps)
    {
        var sh = solid.Shells.Count > 0 ? solid.Shells[0] : solid.AddShell();
        foreach (var f in comps.SelectMany(c => c)) f.Shell = sh;
    }

    /// <summary>Знаковый объём компоненты: точные интегралы граней, иначе — фасеты граней (double, от опорной точки).</summary>
    public static double SignedVolume(List<TFace> comp)
    {
        double s = 0; bool ok = true;
        foreach (var f in comp)
        {
            var c = AnalyticVolume.FaceIntegral(f);
            if (c == null || double.IsNaN(c.Value)) { ok = false; break; }
            s += c.Value;
        }
        if (ok) return s / 3;
        var tris = Triangles(comp);
        if (tris.Count == 0) return 0;
        var o = tris[0].A; double v = 0;
        foreach (var (a, b, c) in tris) v += Vector3d.Dot(a - o, Vector3d.Cross(b - o, c - o));
        return v / 6;
    }

    // g.11: сравнение с многоугольниками — по ФАСЕТАМ граней (они построены из многоугольников), не по видимой сетке:
    // видимая сетка ближе к поверхности и расходилась на 0.25% (тор ∩ шар, серия 9, случай 91) — форма снималась, тело
    // показывалось несогласованными сетками граней. Мутация P3D_SPLITFACETS=0.
    static readonly bool SplitFacetsOld = Environment.GetEnvironmentVariable("P3D_SPLITFACETS") == "0";
    static readonly bool SplitTolOld = Environment.GetEnvironmentVariable("P3D_SPLITTOL") == "0";
    static double FacetArea(List<TFace> comp)
    {
        double a = 0;
        foreach (var (p, q, r) in Triangles(comp, true)) a += Vector3d.Cross(q - p, r - p).Length / 2;
        return a;
    }
    static double FacetVolume(List<TFace> comp)
    {
        var tris = Triangles(comp, !SplitFacetsOld);
        if (tris.Count == 0) return 0;
        var o = tris[0].A; double v = 0;
        foreach (var (a, b, c) in tris) v += Vector3d.Dot(a - o, Vector3d.Cross(b - o, c - o));
        return v / 6;
    }

    static List<(Vector3d A, Vector3d B, Vector3d C)> Triangles(IEnumerable<TFace> faces, bool facets = false)
    {
        var res = new List<(Vector3d, Vector3d, Vector3d)>();
        foreach (var f in faces)
        {
            var mb = new Tess.MeshBuilder();
            if (facets && f.Surface is Surfaces.FacetedSurface fs) fs.TessellateFacetsFlat(f, mb); else f.Surface.TessellateFace(f, mb);
            var md = new Tess.MeshData(); mb.FillInto(md);
            for (int i = 0; i + 2 < md.Indices.Length; i += 3)
                res.Add((mb.PositionsD[md.Indices[i]], mb.PositionsD[md.Indices[i + 1]], mb.PositionsD[md.Indices[i + 2]]));
        }
        return res;
    }

    /// <summary>Точки полости внутри наружного тела: точный классификатор; не решил — чётность луча по фасетам.</summary>
    static readonly bool CavityProbeOld = Environment.GetEnvironmentVariable("P3D_CAVPROBE") == "0";

    static bool Contains(TSolid outer, List<TFace> outerFaces, List<Vector3d> probe)
    {
        int votes = 0, decided = 0;
        try
        {
            // KRN-03/g.14: классификатор — по граням НАРУЖНОЙ оболочки. По всему телу точка полости (центр треугольника
            // сферы-полости без рёбер лежит в пустоте) получала «вне тела», и годная полость отвергалась инвариантом
            // (шар внутри куба детали). Мутация P3D_CAVPROBE=0.
            var cl = CavityProbeOld ? new Analytic.Classifier(outer) : new Analytic.Classifier(outerFaces);
            if (cl.Unsupported == null)
                foreach (var p in probe) { var c = cl.Classify(p); if (c is 1 or -1) { decided++; if (c == 1) votes++; } }
        }
        catch (Exception ex) when (!KernelContext.IsCancellation(ex)) { }
        if (decided > 0) return votes * 2 > decided;
        var tris = Triangles(outerFaces);
        foreach (var p in probe) { decided++; if (PointInMesh(tris, p)) votes++; }
        return votes * 2 > decided;
    }

    static bool PointInMesh(List<(Vector3d A, Vector3d B, Vector3d C)> tris, Vector3d p)
    {
        var dirs = new[] { new Vector3d(0.5383, 0.6190, 0.5719), new Vector3d(-0.7131, 0.2213, 0.6653), new Vector3d(0.1213, -0.8722, 0.4739) };
        int ins = 0, clean = 0;
        foreach (var d0 in dirs)
        {
            var d = d0.Normalized(); int hits = 0; bool dirty = false;
            foreach (var (a, b, c) in tris)
            {
                var e1 = b - a; var e2 = c - a; var h = Vector3d.Cross(d, e2); double det = Vector3d.Dot(e1, h);
                if (Math.Abs(det) < 1e-18) continue;
                var s = p - a; double u = Vector3d.Dot(s, h) / det; if (u < -1e-9 || u > 1 + 1e-9) continue;
                var q = Vector3d.Cross(s, e1); double v = Vector3d.Dot(d, q) / det; if (v < -1e-9 || u + v > 1 + 1e-9) continue;
                double t = Vector3d.Dot(e2, q) / det; if (t <= 1e-12) continue;
                if (u < 1e-9 || v < 1e-9 || u + v > 1 - 1e-9) { dirty = true; break; }
                hits++;
            }
            if (dirty) continue;
            clean++; if ((hits & 1) == 1) ins++;
        }
        return clean > 0 && ins * 2 > clean;
    }

    /// <summary>
    /// Точная многогранная форма — по связности вершин реестра; компонента многоугольников сопоставляется телу по
    /// габариту и объёму. Неоднозначно — false (форма снимается с тел).
    /// </summary>
    static bool SplitExact(TSolid src, List<TSolid> bodies, out string why)
    {
        why = "";
        var ex = src.Exact!; var R = ex.Solid.Registry; var polys = ex.Solid.Polygons;
        var parent = Enumerable.Range(0, polys.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        var byVertex = new Dictionary<int, int>();
        for (int i = 0; i < polys.Count; i++)
            foreach (int v in polys[i].Vertices(R).ToArray())
                if (byVertex.TryGetValue(v, out int j)) { int a = Find(i), b = Find(j); if (a != b) parent[a] = b; } else byVertex[v] = i;
        var groups = Enumerable.Range(0, polys.Count).GroupBy(Find).Select(g => g.ToList()).ToList();
        // объём и габарит каждой группы
        var info = groups.Select(g =>
        {
            var lo = new Vector3d(double.MaxValue); var hi = new Vector3d(double.MinValue); double vol = 0; Vector3d? o = null;
            foreach (int i in g)
            {
                var ids = polys[i].Vertices(R).ToArray();
                var pts = ids.Select(id => { var (x, y, z) = R.ToMm(id); return new Vector3d(x, y, z); }).ToList();
                foreach (var p in pts) { lo = Vector3d.ComponentMin(lo, p); hi = Vector3d.ComponentMax(hi, p); }
                o ??= pts[0];
                for (int k = 1; k + 1 < pts.Count; k++) vol += Vector3d.Dot(pts[0] - o.Value, Vector3d.Cross(pts[k] - o.Value, pts[k + 1] - o.Value));
            }
            return (Lo: lo, Hi: hi, Vol: vol / 6);
        }).ToList();
        // тело ← группы: габарит группы внутри габарита наружной оболочки тела, сумма объёмов — объём тела
        var assigned = new int[groups.Count]; Array.Fill(assigned, -1);
        for (int b = 0; b < bodies.Count; b++)
        {
            var (blo, bhi) = Box(bodies[b].Shells[0].Faces);
            var pad = new Vector3d(1e-3 + 1e-6 * (bhi - blo).Length);
            for (int g = 0; g < groups.Count; g++)
                if (Inside(info[g].Lo, info[g].Hi, blo - pad, bhi + pad))
                {
                    if (assigned[g] >= 0 && assigned[g] != b)
                    {
                        // во вложенных габаритах — меньшему телу (остров в полости)
                        var (alo, ahi) = Box(bodies[assigned[g]].Shells[0].Faces);
                        if (Volume(bhi - blo) < Volume(ahi - alo)) assigned[g] = b;
                    }
                    else assigned[g] = b;
                }
        }
        if (assigned.Any(a => a < 0)) { why = "a polygon group matches no body"; return Drop(bodies); }
        for (int b = 0; b < bodies.Count; b++)
        {
            // сравнение представления с представлением: фасеты B-rep (построены из этих многоугольников) с многоугольниками,
            // а не точный объём поверхностей (у шара 523.6 против 519.9 по гранению)
            double bv = bodies[b].Shells.Sum(sh => FacetVolume(sh.Faces.ToList()));
            double pv = Enumerable.Range(0, groups.Count).Where(g => assigned[g] == b).Sum(g => info[g].Vol);
            // g.11: допуск — по отходу фасет от многоугольников (хорда × площадь), а не 1e-3 доли: фасеты граней после
            // восстановления дуг (сцепки хорд → дуги) переразмечаются, и у малого тела (брусок минус тор, 103 мм³) расхождение
            // 0.3% снимало форму — тело показывалось несогласованными сетками граней (164 стороны без пары). Проверка ловит
            // грубое: группа многоугольников не у того тела. Мутация P3D_SPLITTOL=0.
            double tolV = 1e-3 * Math.Max(1, Math.Abs(bv));
            if (!SplitTolOld) tolV = Math.Max(tolV, 2 * Parametric3D.Core.Tol.Chord * bodies[b].Shells.Sum(sh => FacetArea(sh.Faces.ToList())));
            if (Math.Abs(bv - pv) > tolV) { why = $"body {b}: B-rep volume {bv:G8} vs polygons {pv:G8}"; return Drop(bodies); }
        }
        for (int b = 0; b < bodies.Count; b++)
        {
            var mine = Enumerable.Range(0, groups.Count).Where(g => assigned[g] == b).SelectMany(g => groups[g]).Select(i => polys[i]);
            bodies[b].Exact = new Exact.ExactBody(new Exact.ExactSolid(R, mine), ex.Sources);
        }
        return true;
    }

    static bool Drop(List<TSolid> bodies) { foreach (var b in bodies) b.Exact = null; return false; }
    static double Volume(Vector3d d) => Math.Abs(d.X * d.Y * d.Z);
    static bool Inside(Vector3d lo, Vector3d hi, Vector3d blo, Vector3d bhi) =>
        lo.X >= blo.X && lo.Y >= blo.Y && lo.Z >= blo.Z && hi.X <= bhi.X && hi.Y <= bhi.Y && hi.Z <= bhi.Z;
    static (Vector3d Lo, Vector3d Hi) Box(IEnumerable<TFace> faces)
    {
        var lo = new Vector3d(double.MaxValue); var hi = new Vector3d(double.MinValue);
        foreach (var f in faces)
            foreach (var e in f.Edges)
                foreach (var p in new[] { e.V0.P, e.V1.P }) { lo = Vector3d.ComponentMin(lo, p); hi = Vector3d.ComponentMax(hi, p); }
        foreach (var f in faces)
            if (f.Surface is Surfaces.FacetedSurface fs)
                foreach (var (a, b, c) in fs.Facets) foreach (var p in new[] { a, b, c }) { lo = Vector3d.ComponentMin(lo, p); hi = Vector3d.ComponentMax(hi, p); }
        return (lo, hi);
    }
}
