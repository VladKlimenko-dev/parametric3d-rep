using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;

/// <summary>
/// k140 (замечание 30.10, 07:01): КОРПУС ЛОФТОВ ДЛЯ СКРУГЛЕНИЙ И ФАСОК. Десятки лофтов по зерну: 2–3 сечения
/// (многоугольники 3–8 сторон, прямоугольники, окружности; сдвиг, поворот, масштаб), с отверстием и без, гладкие и
/// линейчатые. На каждом — скругления и фаски рёбер торцов (по одному ребру и весь контур). Замеры: принята ли правка,
/// тело годное, точное или многогранное, время, изменение объёма против формулы по двугранному углу (интеграл вдоль
/// ребра), лишние мелкие грани. Отказы и несоответствия пишутся строками JSON; модели с ошибкой — в .p3d для разбора.
/// </summary>
static partial class Program
{
    sealed record CorpusModel(int Seed, string Desc, Part Part);

    /// <summary>Модель корпуса по зерну.</summary>
    static CorpusModel CorpusMake(int seed)
    {
        var rnd = new Random(seed * 7919 + 13);
        double R(double a, double b) => a + (b - a) * rnd.NextDouble();
        var p = new Part($"corpus{seed}");
        int nSec = rnd.Next(2, 4);
        bool holes = rnd.NextDouble() < 0.6;
        bool smooth = nSec == 3 ? rnd.NextDouble() < 0.6 : rnd.NextDouble() < 0.3;
        bool tangent = smooth && rnd.NextDouble() < 0.5;
        var desc = new List<string>();
        double z = 0;
        var secIds = new List<uint>();
        Vector2d off = Vector2d.Zero;
        for (int k = 0; k < nSec; k++)
        {
            if (k > 0) z -= R(40, 110);
            var f = new Frame3(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY);
            var s = new Sketch($"S{k + 1}", f); p.Add(s);
            double size = R(18, 45);
            if (k > 0) off += new Vector2d(R(-12, 12), R(-12, 12));
            int kind = rnd.Next(0, 4);   // 0 — многоугольник, 1 — прямоугольник, 2 — окружность, 3 — неправильный многоугольник
            int sides = kind == 2 ? 0 : kind == 1 ? 4 : rnd.Next(3, 9);
            double rot = R(0, Math.PI);
            double aspect = R(0.5, 1);
            string sd;
            if (kind == 2) { s.AddEntity(new SkCircle(new SkPoint(off.X, off.Y), size)); sd = $"circle R{size:0.#}"; }
            else
            {
                var pts = new List<SkPoint>();
                for (int i = 0; i < sides; i++)
                {
                    double a = rot + 2 * Math.PI * i / sides + (kind == 3 ? R(-0.25, 0.25) * Math.PI / sides : 0);
                    double rr = kind == 1 ? size / Math.Cos(Math.PI / 4) : kind == 3 ? size * R(0.8, 1.15) : size;
                    double x = Math.Cos(a) * rr, y = Math.Sin(a) * rr * (kind == 1 ? aspect : 1);
                    pts.Add(new SkPoint(off.X + x, off.Y + y));
                }
                for (int i = 0; i < sides; i++) s.AddEntity(new SkLine(pts[i], pts[(i + 1) % sides]));
                sd = kind == 1 ? $"rect {size * 2:0.#}x{size * 2 * aspect:0.#}" : $"{(kind == 3 ? "irregular " : "")}{sides}-gon R{size:0.#}";
            }
            if (holes)
            {
                double hs = size * R(0.25, 0.42);
                var ho = off + new Vector2d(R(-0.15, 0.15) * size, R(-0.15, 0.15) * size);
                int hk = rnd.Next(0, 3);
                if (hk == 2) { s.AddEntity(new SkCircle(new SkPoint(ho.X, ho.Y), hs)); sd += $" / hole circle R{hs:0.#}"; }
                else
                {
                    int hsides = hk == 1 ? 4 : rnd.Next(3, 7); double hrot = R(0, Math.PI);
                    var hp = Enumerable.Range(0, hsides).Select(i => new SkPoint(ho.X + Math.Cos(hrot + 2 * Math.PI * i / hsides) * hs, ho.Y + Math.Sin(hrot + 2 * Math.PI * i / hsides) * hs)).ToList();
                    for (int i = 0; i < hsides; i++) s.AddEntity(new SkLine(hp[i], hp[(i + 1) % hsides]));
                    sd += $" / hole {hsides}-gon R{hs:0.#}";
                }
            }
            desc.Add(sd);
            secIds.Add(s.Id);
        }
        var lf = new LoftFeature("Loft") { Smooth = smooth, StartTangent = tangent ? 0.5 : 0, EndTangent = tangent ? 0.5 : 0 };
        lf.SectionSketchIds.AddRange(secIds);
        p.AddFeature(lf);
        return new CorpusModel(seed, $"{(smooth ? "smooth" : "ruled")}{(tangent ? "+tangent" : "")}: " + string.Join(" → ", desc), p);
    }

    /// <summary>Цепочка касательных рёбер (как Topo.TangentChain приложения: |cos| ≥ 0.99985 в общей вершине).</summary>
    static List<TEdge> CorpusChain(TEdge start, List<TEdge> all)
    {
        Vector3d? Dir(TEdge e, TVertex v)
        {
            if (e.Curve == null) return null;
            bool at0 = ReferenceEquals(e.V0, v) || (e.V0.P - v.P).Length < 1e-7;
            var d = at0 ? e.Curve.TangentAt(e.Curve.T0) : e.Curve.TangentAt(e.Curve.T1);
            return d.LengthSquared > 0 ? d.Normalized() : null;
        }
        var res = new List<TEdge> { start }; var seen = new HashSet<TEdge> { start }; var q = new Queue<TEdge>(); q.Enqueue(start);
        while (q.Count > 0)
        {
            var e = q.Dequeue();
            foreach (var v in new[] { e.V0, e.V1 })
            {
                if (Dir(e, v) is not { } d) continue;
                foreach (var o in all)
                {
                    if (seen.Contains(o) || !(ReferenceEquals(o.V0, v) || ReferenceEquals(o.V1, v) || (o.V0.P - v.P).Length < 1e-7 || (o.V1.P - v.P).Length < 1e-7)) continue;
                    if (Dir(o, v) is not { } d2 || Math.Abs(Vector3d.Dot(d, d2)) < 0.99985) continue;
                    // k140: в цепочку — только рёбра, острые по всей длине (как и первое): ребро, сходящее на нет, постоянным
                    // размером не обрабатывается
                    if (!SharpEverywhere(o)) continue;
                    seen.Add(o); res.Add(o); q.Enqueue(o);
                }
            }
        }
        return res;
    }

    /// <summary>Изменение объёма по формуле: интеграл площади среза по ребру (двугранный угол — по нормалям граней).</summary>
    static double? CorpusExpected(TEdge e, bool round, double size)
    {
        // k140: СЕЧЕНИЕ ПО НАСТОЯЩИМ ГРАНЯМ. В плоскости поперёк ребра — профили обеих граней (ломаные по точкам поверхности
        // в этой плоскости), окружность радиуса r, касательная к обоим (катящийся шар в сечении), площадь между профилями
        // и дугой; у фаски — отрезок между точками на расстоянии d по профилям. Объём — интеграл площади по ребру с
        // поправкой Паппа на кривизну ребра (центр тяжести среза). Независимо от резца ядра.
        var faces = e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList();
        if (faces.Count != 2 || e.Curve == null) return null;
        const int N = 12;
        var c3 = e.Curve; double sum = 0;
        for (int i = 0; i < N; i++)
        {
            double t0 = c3.T0 + (c3.T1 - c3.T0) * i / N, t1 = c3.T0 + (c3.T1 - c3.T0) * (i + 1) / N, tm = (t0 + t1) / 2;
            double ds = (c3.PointAt(t1) - c3.PointAt(t0)).Length;
            var p = c3.PointAt(tm); var T = c3.TangentAt(tm).Normalized();
            var prof = new List<List<Vector2d>>(); Vector3d U = Vector3d.Zero, V = Vector3d.Zero; bool okAll = true;
            var nn = new List<Vector3d>();
            foreach (var f in faces)
            {
                var sf = GeometryAudit.AuthoritativeSurface(f) ?? f.Surface;
                var n = sf.NormalAt(sf.ProjectToUV(p)); if (f.Reversed) n = -n; nn.Add(n.Normalized());
            }
            // направления вглубь граней в плоскости сечения
            var dirs = new List<Vector3d>();
            for (int k = 0; k < 2; k++)
            {
                var d = Vector3d.Cross(T, nn[k]).Normalized();
                // внутрь грани k: против нормали другой грани
                if (Vector3d.Dot(d, nn[1 - k]) > 0) d = -d;
                dirs.Add(d);
            }
            U = dirs[0]; V = (dirs[1] - U * Vector3d.Dot(dirs[1], U)); if (V.LengthSquared < 1e-12) return null; V.Normalize();
            for (int k = 0; k < 2; k++)
            {
                // профиль может перейти на соседнюю грань (гладкий шов лофта) — ближайшая проекция среди своей и соседних
                var cands = new List<Parametric3D.Kernel.Surfaces.ISurface3> { GeometryAudit.AuthoritativeSurface(faces[k]) ?? faces[k].Surface };
                foreach (var ed in faces[k].Edges)
                    foreach (var co in ed.CoEdges)
                        if (co.Loop?.Face is { } nf && !ReferenceEquals(nf, faces[k]) && !ReferenceEquals(nf, faces[1 - k]))
                            cands.Add(GeometryAudit.AuthoritativeSurface(nf) ?? nf.Surface);
                Vector3d Onto(Vector3d x) { Vector3d best = x; double bd = double.MaxValue; foreach (var c in cands) { var q2 = c.PointAt(c.ProjectToUV(x)); double d2 = (q2 - x).LengthSquared; if (d2 < bd) { bd = d2; best = q2; } } return best; }
                var pl = new List<Vector2d> { Vector2d.Zero };
                double step = Math.Max(size, 0.01) * 0.04; var cur = p;
                for (int j = 1; j <= 75; j++)
                {
                    var q = cur + dirs[k] * step;
                    for (int it = 0; it < 4; it++) { q = Onto(q); q -= T * Vector3d.Dot(q - p, T); }
                    var dd = q - cur; if (dd.Length < step * 0.2) { if (j * step < 1.6 * size) { okAll = false; if (Environment.GetEnvironmentVariable("CORPUSDBG") == "1") Console.WriteLine($"   PROFILE stall face {faces[k].Tag} at {j * step:0.###} of size {size} p {p}"); } break; }
                    dirs[k] = dd.Normalized(); cur = q;
                    pl.Add(new Vector2d(Vector3d.Dot(q - p, U), Vector3d.Dot(q - p, V)));
                }
                prof.Add(pl);
            }
            if (!okAll) return null;
            double area; Vector2d cen;
            if (!round)
            {
                Vector2d At(List<Vector2d> pl, double d) { double acc = 0; for (int j = 1; j < pl.Count; j++) { double l = (pl[j] - pl[j - 1]).Length; if (acc + l >= d) return pl[j - 1] + (pl[j] - pl[j - 1]) * ((d - acc) / l); acc += l; } return pl[^1]; }
                var a1 = At(prof[0], size); var a2 = At(prof[1], size);
                var poly = new List<Vector2d> { Vector2d.Zero };
                double acc1 = 0; for (int j = 1; j < prof[0].Count; j++) { acc1 += (prof[0][j] - prof[0][j - 1]).Length; if (acc1 >= size) break; poly.Add(prof[0][j]); }
                poly.Add(a1); poly.Add(a2);
                var back = new List<Vector2d>(); double acc2 = 0; for (int j = 1; j < prof[1].Count; j++) { acc2 += (prof[1][j] - prof[1][j - 1]).Length; if (acc2 >= size) break; back.Add(prof[1][j]); }
                back.Reverse(); poly.AddRange(back);
                (area, cen) = PolyAreaCentroid(poly);
            }
            else
            {
                // центр окружности: точка, удалённая на r от обоих профилей (смещения внутрь угла), — поиск по сетке и уточнение
                double Dist(List<Vector2d> pl, Vector2d x, out Vector2d foot)
                {
                    double best = double.MaxValue; foot = pl[0];
                    for (int j = 1; j < pl.Count; j++)
                    {
                        var a = pl[j - 1]; var b = pl[j]; var ab = b - a; double tt = Math.Clamp(Vector2d.Dot(x - a, ab) / ab.LengthSquared, 0, 1);
                        var q = a + ab * tt; double d = (x - q).Length; if (d < best) { best = d; foot = q; }
                    }
                    return best;
                }
                // начальная точка: биссектриса
                var bis = (prof[0][1].Normalized() + prof[1][1].Normalized()).Normalized();
                double ang = Math.Acos(Math.Clamp(Vector2d.Dot(prof[0][1].Normalized(), prof[1][1].Normalized()), -1, 1));
                var c0 = bis * (size / Math.Sin(ang / 2));
                for (int it = 0; it < 60; it++)
                {
                    double d1 = Dist(prof[0], c0, out var f1) - size, d2 = Dist(prof[1], c0, out var f2) - size;
                    if (Math.Abs(d1) < 1e-10 && Math.Abs(d2) < 1e-10) break;
                    var g1 = (c0 - f1).Normalized(); var g2 = (c0 - f2).Normalized();
                    double det = g1.X * g2.Y - g1.Y * g2.X; if (Math.Abs(det) < 1e-12) break;
                    var dc = new Vector2d((-d1 * g2.Y + d2 * g1.Y) / det, (-g1.X * d2 + g2.X * d1) / det);
                    c0 += dc;
                }
                Dist(prof[0], c0, out var t1p); Dist(prof[1], c0, out var t2p);
                var poly = new List<Vector2d> { Vector2d.Zero };
                foreach (var q in prof[0].Skip(1)) { if ((q).Length >= t1p.Length) break; poly.Add(q); }
                poly.Add(t1p);
                double aA = Math.Atan2(t1p.Y - c0.Y, t1p.X - c0.X), aB = Math.Atan2(t2p.Y - c0.Y, t2p.X - c0.X);
                double sw = aB - aA; while (sw > Math.PI) sw -= 2 * Math.PI; while (sw < -Math.PI) sw += 2 * Math.PI;
                for (int j = 1; j < 24; j++) { double aa = aA + sw * j / 24; poly.Add(c0 + new Vector2d(Math.Cos(aa), Math.Sin(aa)) * size); }
                poly.Add(t2p);
                var back = new List<Vector2d>(); foreach (var q in prof[1].Skip(1)) { if (q.Length >= t2p.Length) break; back.Add(q); }
                back.Reverse(); poly.AddRange(back);
                (area, cen) = PolyAreaCentroid(poly);
            }
            // поправка Паппа: кривизна ребра в плоскости сечения
            double h = 1e-4 * (c3.T1 - c3.T0);
            var dT = (c3.TangentAt(Math.Min(c3.T1, tm + h)).Normalized() - c3.TangentAt(Math.Max(c3.T0, tm - h)).Normalized()) / ((c3.PointAt(Math.Min(c3.T1, tm + h)) - c3.PointAt(Math.Max(c3.T0, tm - h))).Length + 1e-300);
            var cen3 = U * cen.X + V * cen.Y;
            sum += Math.Abs(area) * (1 - Vector3d.Dot(dT, cen3)) * ds;
        }
        return sum;
    }

    /// <summary>Законный отказ по размеру: размер больше грани или ребра, резец складывается на крутом повороте цепочки.</summary>
    static bool LegitRefusal(string err) =>
        err.Contains("too short for a cut") || err.Contains("which is only") || err.Contains("folds over itself") || err.Contains("wider than")
        // k140: цепочка поворачивает круче размера (радиус кривизны цепочки меньше размера) — шар не помещается; законный отказ
        || err.Contains("would intersect itself");

    /// <summary>Ребро острое по всей длине (угол граней больше 2° в 9 точках): ребро, сходящее на нет (гладкий участок шва
    /// лофта), скругляется постоянным радиусом только частично — законный отказ, в корпус не идёт.</summary>
    static bool SharpEverywhere(TEdge e)
    {
        var faces = e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList();
        if (faces.Count != 2 || e.Curve == null) return false;
        for (int i = 0; i <= 10; i++)
        {
            // и в самих концах: шов лофта, сходящий на нет у сечения-окружности, — не ребро постоянного скругления
            double tt = i == 0 ? 0 : i == 10 ? 1 : 0.02 + 0.96 * (i - 1) / 8.0;
            var q = e.Curve.PointAt(e.Curve.T0 + (e.Curve.T1 - e.Curve.T0) * tt);
            var n = faces.Select(f => { var s = GeometryAudit.AuthoritativeSurface(f) ?? f.Surface; return s.NormalAt(s.ProjectToUV(q)).Normalized(); }).ToList();
            if (Math.Abs(Vector3d.Dot(n[0], n[1])) > Math.Cos((tt == 0 || tt == 1 ? 5 : 2) * Math.PI / 180)) return false;
        }
        return true;
    }
    static double EdgeLen(TEdge e) { var c = e.Curve!; double l = 0; var pr = c.PointAt(c.T0); for (int i = 1; i <= 16; i++) { var q = c.PointAt(c.T0 + (c.T1 - c.T0) * i / 16.0); l += (q - pr).Length; pr = q; } return l; }

    /// <summary>Шов гладкий: нормали граней у середины ребра почти совпадают.</summary>
    static bool SmoothJoin(TEdge e) => !Program.Sharp(e);

    static (double Area, Vector2d Centroid) PolyAreaCentroid(List<Vector2d> pts)
    {
        double a = 0, cx = 0, cy = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            var p = pts[i]; var q = pts[(i + 1) % pts.Count]; double cr = p.X * q.Y - q.X * p.Y;
            a += cr; cx += (p.X + q.X) * cr; cy += (p.Y + q.Y) * cr;
        }
        a /= 2; if (Math.Abs(a) < 1e-300) return (0, Vector2d.Zero);
        return (a, new Vector2d(cx / (6 * a), cy / (6 * a)));
    }

    static void LoftCorpusRun(int count, int seed0, int maxEdges, string outDir)
    {
        Directory.CreateDirectory(outDir);
        int nOk = 0, nBad = 0, nRefused = 0, nLegit = 0; var why = new Dictionary<string, int>();
        double totalMs = 0;
        for (int sd = seed0; sd < seed0 + count; sd++)
        {
            CorpusModel m;
            var sw0 = System.Diagnostics.Stopwatch.StartNew();
            try { m = CorpusMake(sd); m.Part.Rebuild(null); }
            catch (Exception ex) { Log(new { seed = sd, loft = "exception " + ex.Message }); continue; }
            var lfe = m.Part.Features[0];
            double loftMs = sw0.ElapsedMilliseconds;
            if (lfe.Error != null || m.Part.Bodies.Count != 1)
            {
                Log(new { seed = sd, m.Desc, loft = "refused", error = lfe.Error?.Split('\n').Last(), loftMs });
                continue;
            }
            var body0 = m.Part.Bodies[0];
            bool lValid = SolidValidator.Validate(body0).IsValid;
            Log(new { seed = sd, m.Desc, loft = "ok", loftMs, guarantee = body0.Guarantee.ToString(), valid = lValid, faces = body0.Faces.Count(), volume = Math.Round(m.Part.Volume, 3) });
            if (!lValid) { nBad++; why["loft invalid"] = why.GetValueOrDefault("loft invalid") + 1; }
            string text = DocumentFile.Save(m.Part);
            // рёбра торцов: у плоских граней лофта
            var capEdges = body0.Edges.Where(e => e.CoEdges.Any(c => c.Loop?.Face?.Tag is { } tg && (tg.EndsWith("loft:start") || tg.EndsWith("loft:end"))) && Program.Sharp(e) && SharpEverywhere(e)).ToList();
            var rnd = new Random(sd);
            var picks = capEdges.OrderBy(_ => rnd.Next()).Take(maxEdges).ToList();
            // кратчайшее ребро торцов и толщина стенки — масштаб размера
            static double CurveLen(TEdge e) { var c = e.Curve!; double l = 0; var pr = c.PointAt(c.T0); for (int i = 1; i <= 16; i++) { var q = c.PointAt(c.T0 + (c.T1 - c.T0) * i / 16.0); l += (q - pr).Length; pr = q; } return l; }
            double minEdge = capEdges.Where(e => e.Curve != null).Select(CurveLen).DefaultIfEmpty(10).Min();
            foreach (var e0 in picks)
                foreach (bool round in new[] { true, false })
                    foreach (double frac in new[] { 0.08, 0.2 })
                    {
                        var A = e0.V0.P; var B = e0.V1.P; var mid = e0.Curve!.PointAt(0.5 * (e0.Curve.T0 + e0.Curve.T1));
                        double size = Math.Round(Math.Max(0.3, frac * Math.Min(minEdge, 30)), 2);
                        var (p, pars) = DocumentFile.Load(text); p.Rebuild(pars);
                        var e = p.Bodies.SelectMany(b => b.Edges).OrderBy(q => (q.Curve!.PointAt(0.5 * (q.Curve.T0 + q.Curve.T1)) - mid).Length + Math.Min((q.V0.P - A).Length + (q.V1.P - B).Length, (q.V0.P - B).Length + (q.V1.P - A).Length)).First();
                        // как в приложении (выбор с цепочкой, AutoChain): ребро и касательно продолжающие его
                        var chain = CorpusChain(e, p.Bodies.SelectMany(b => b.Edges).ToList());
                        bool closedChain = chain.All(ce => chain.Count(o => ReferenceEquals(o.V0, ce.V0) || ReferenceEquals(o.V1, ce.V0) || (o.V0.P - ce.V0.P).Length < 1e-7 || (o.V1.P - ce.V0.P).Length < 1e-7) >= 2
                                                        && chain.Count(o => ReferenceEquals(o.V0, ce.V1) || ReferenceEquals(o.V1, ce.V1) || (o.V0.P - ce.V1.P).Length < 1e-7 || (o.V1.P - ce.V1.P).Length < 1e-7) >= 2);
                        double? expect = 0;
                        foreach (var ce in chain) { var x = CorpusExpected(ce, round, size); expect = x == null || expect == null ? null : expect + x; }
                        double v0 = p.Volume; int f0 = p.Bodies[0].Faces.Count();
                        var before = TopologyClone.Deep(p.Bodies[0]);
                        Feature f = round ? new RoundFeature("R") { Radius = new ValueRef(size) } : new ChamferFeature("C") { D1 = new ValueRef(size), D2 = new ValueRef(size) };
                        foreach (var ce in chain) { if (f is RoundFeature rf) rf.Edges.Add(EdgeRef.Of(ce)); else ((ChamferFeature)f).Edges.Add(EdgeRef.Of(ce)); }
                        p.AddFeature(f);
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        string? drWhy = null; var drPrev = Parametric3D.Kernel.Ops.DirectRound.Debug;
                        Parametric3D.Kernel.Ops.DirectRound.Debug = s => { if (Environment.GetEnvironmentVariable("LOFT_DRDEBUG") is { } dd && s.Contains(dd)) Console.WriteLine("DBG " + (s.Length > 300 ? s[..300] : s)); if (s.StartsWith("declined")) drWhy = s.Length > 200 ? s[..200] : s; };
                        try { p.Rebuild(pars); }
                        catch (Exception ex) when (ex is not OutOfMemoryException) { f.Error = "exception: " + ex.Message; }
                        finally { Parametric3D.Kernel.Ops.DirectRound.Debug = drPrev; }
                        double ms = sw.ElapsedMilliseconds; totalMs += ms;
                        string tag = $"s{sd} {(round ? "R" : "C")}{size} edge ({mid.X:0.##},{mid.Y:0.##},{mid.Z:0.##}){(chain.Count > 1 ? $" chain {chain.Count}" : "")}";
                        var problems = new List<string>();
                        if (f.Error != null && LegitRefusal(f.Error)) { nLegit++; Log(new { tag, ms, legit = f.Error.Split('\n').Last(), full = f.Error }); continue; }
                        if (f.Error != null)
                        {
                            nRefused++; string key = System.Text.RegularExpressions.Regex.Replace(f.Error.Split('\n').Last(), @"[-0-9]+\.[0-9]+(E[-+0-9]+)?", "#");
                            if (key.Length > 140) key = key[..140];
                            why[key] = why.GetValueOrDefault(key) + 1;
                            problems.Add("refused: " + f.Error.Split('\n').Last());
                        }
                        else if (p.Bodies.Count != 1)
                        {
                            problems.Add($"the body split into {p.Bodies.Count} bodies");
                            Log(new { tag, ms, problems });
                        }
                        else
                        {
                            var b = p.Bodies.Single(); var vr = SolidValidator.Validate(b);
                            // объём «до» — тем же способом, что «после»: у многогранного итога — по сетке (прежние грани сохраняют
                            // свои треугольники), у точного — точный
                            static double MeshVol(TSolid t) => Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(t));
                            double dv = b.Guarantee.ToString() == "ExactAnalytic" ? Math.Abs(p.Volume - v0) : Math.Abs(MeshVol(b) - MeshVol(before));
                            if (!vr.IsValid) problems.Add("invalid: " + string.Join(" / ", vr.Issues.Where(i => i.Severity == ValidationSeverity.Error).Take(2)));
                            if (expect is { } ex && double.IsFinite(ex))
                            {
                                double tol = 0.03 * ex + (closedChain ? 0 : 2.5 * size * size * size) + 0.02
                                    // гранение граней (допуск хорды) против точного сечения: полоса шириной хорды на половине ширины касания вдоль цепочки
                                    + 0.5 * Parametric3D.Core.Tol.Chord * size * chain.Sum(EdgeLen);
                                if (Math.Abs(dv - ex) > tol) problems.Add($"volume change {dv:0.###} vs formula {ex:0.###}");
                            }
                            // мелкие грани у ребра
                            // щепка: площадь меньше 10⁻⁴ r² или средняя ширина (2·площадь/периметр) меньше 0.005 r
                            bool Tiny(TFace fz)
                            {
                                if (AnalyticVolume.FaceAreaRough(fz) is not { } aa) return false;
                                double per = 0; foreach (var ce in fz.Loops.SelectMany(l => l.CoEdges)) { var pts = new List<Vector3d>(); ce.Tessellate(pts, true); for (int k = 1; k < pts.Count; k++) per += (pts[k] - pts[k - 1]).Length; }
                                return Math.Abs(aa) < 1e-4 * size * size || (per > 0 && 2 * Math.Abs(aa) / per < 0.005 * size);
                            }
                            int tiny = b.Faces.Count(Tiny);
                            if (tiny > 0)
                            {
                                problems.Add($"{tiny} tiny face(s)");
                                foreach (var fz in b.Faces.Where(Tiny).Take(3))
                                {
                                    var pts = new List<Vector3d>(); foreach (var ce in fz.Loops.SelectMany(l => l.CoEdges)) ce.Tessellate(pts, true);
                                    var c = pts.Count > 0 ? pts.Aggregate(Vector3d.Zero, (x, y) => x + y) / pts.Count : Vector3d.Zero;
                                    Console.WriteLine($"   TINY {fz.Tag} {GeometryAudit.AuthoritativeSurface(fz)?.GetType().Name} area {AnalyticVolume.FaceAreaRough(fz):G3} at ({c.X:0.###},{c.Y:0.###},{c.Z:0.###}) edges {fz.Loops.Sum(l => l.CoEdges.Count)}");
                                }
                            }
                            Log(new { tag, ms, guarantee = b.Guarantee.ToString(), dv = Math.Round(dv, 4), expect = expect is { } ee ? Math.Round(ee, 4) : (double?)null, faces = $"{f0}->{b.Faces.Count()}", problems, dr = drWhy });
                        }
                        if (problems.Count > 0 && f.Error != null) Log(new { tag, ms, problems });
                        if (Environment.GetEnvironmentVariable("CORPUS_SAVEALL") == "1")
                            try { File.WriteAllText(Path.Combine(outDir, $"case_s{sd}_{(round ? "R" : "C")}{size}_{Math.Abs(mid.GetHashCode()) % 100000}.p3d"), DocumentFile.Save(p, pars)); } catch { }
                        if (problems.Count == 0) nOk++;
                        else
                        {
                            nBad += f.Error == null ? 1 : 0;
                            foreach (var pr in problems.Where(x => !x.StartsWith("refused")))
                            {
                                string key = System.Text.RegularExpressions.Regex.Replace(pr, @"[-0-9]+\.[0-9]+(E[-+0-9]+)?", "#"); if (key.Length > 140) key = key[..140];
                                why[key] = why.GetValueOrDefault(key) + 1;
                            }
                            try { File.WriteAllText(Path.Combine(outDir, $"fail_s{sd}_{(round ? "R" : "C")}{size}_{Math.Abs(mid.GetHashCode()) % 100000}.p3d"), DocumentFile.Save(p, pars)); } catch { }
                        }
                    }
        }
        Log(new { ok = nOk, bad = nBad, refused = nRefused, legit = nLegit, totalOpSeconds = Math.Round(totalMs / 1000, 1) });
        foreach (var kv in why.OrderByDescending(k => k.Value)) Console.WriteLine($"{kv.Value,4}  {kv.Key}");
    }
}

static partial class Program
{
    /// <summary>Разбор случая корпуса: последняя операция файла — проверяемая. OBJ до, после и резца; время, объёмы.</summary>
    static double TriDist(Vector3d a, Vector3d b, Vector3d c, Vector3d q)
    {
        // расстояние до треугольника — по сетке барицентрических точек (грубо, для разбора)
        double best = 1e9;
        for (int i = 0; i <= 40; i++) for (int j = 0; i + j <= 40; j++)
        {
            var x = a + (b - a) * (i / 40.0) + (c - a) * (j / 40.0);
            best = Math.Min(best, (x - q).Length);
        }
        return best;
    }

    static double SegDist(TEdge e, Vector3d q)
    {
        double best = 1e9; Vector3d prev = e.Curve.PointAt(e.Curve.T0);
        for (int i = 1; i <= 512; i++)
        {
            var cur = e.Curve.PointAt(e.Curve.T0 + (e.Curve.T1 - e.Curve.T0) * i / 512.0);
            var d = cur - prev; double t = d.LengthSquared < 1e-30 ? 0 : Math.Clamp(Vector3d.Dot(q - prev, d) / d.LengthSquared, 0, 1);
            best = Math.Min(best, (prev + d * t - q).Length); prev = cur;
        }
        return best;
    }

    static void CorpusCase(string file, string outPrefix)
    {
        var text = File.ReadAllText(file);
        var (p0, pars0) = DocumentFile.Load(text);
        p0.StageIndex = p0.Features.Count - 1; p0.Rebuild(pars0);
        double v0 = p0.Volume; WriteObj(outPrefix + "_before.obj", p0.Bodies);
        if (Environment.GetEnvironmentVariable("EDGEPAIR") is { } ep)
        {
            var two = ep.Split('|');
            foreach (var e in p0.Bodies.SelectMany(b => b.Edges))
            {
                var tags = e.CoEdges.Select(c => c.Loop?.Face?.Tag ?? "").ToList();
                if (tags.Any(t => t.EndsWith(two[0])) && tags.Any(t => t.EndsWith(two[1])))
                    Console.WriteLine($"PAIR e{e.Id} {e.Curve?.GetType().Name} {e.V0.P:0.####}->{e.V1.P:0.####} len {(e.V1.P - e.V0.P).Length:0.####} faces {string.Join(" | ", tags)}");
            }
        }
        var (p, pars) = DocumentFile.Load(text);
        TSolid? tool = null;
        Parametric3D.Kernel.Ops.FilletOp.BuiltTool = t => tool = TopologyClone.Deep(t);   // последний резец — операции файла
        if (Environment.GetEnvironmentVariable("SWEEPDBG") == "1") Parametric3D.Kernel.Ops.FilletOp.SweepDebug = m => Console.WriteLine("SWEEP " + m);
        if (Environment.GetEnvironmentVariable("REACHDBG") == "1") Parametric3D.Kernel.Ops.FilletOp.ReachDebug = m => Console.WriteLine("REACH " + m);
        if (Environment.GetEnvironmentVariable("BALLDBG") == "1") Parametric3D.Kernel.Ops.FilletOp.BallDebug = m => Console.WriteLine("BALL " + m);
        if (Environment.GetEnvironmentVariable("DRDBG") == "1") Parametric3D.Kernel.Ops.DirectRound.Debug = m => Console.WriteLine("DR " + m);
        if (Environment.GetEnvironmentVariable("ROUTEDBG") == "1")
        {
            Parametric3D.Kernel.Ops.BooleanOp.AnalyticRouteDebug = m => Console.WriteLine("ROUTE " + m);
            Parametric3D.Kernel.Ops.BooleanOp.PathDebug = m => Console.WriteLine("PATH " + m);
        }
        if (Environment.GetEnvironmentVariable("AREADBG") is { Length: > 0 } adbg) Parametric3D.Kernel.Tess.ExactFaceMesher.AreaDebug = m => { if (adbg == "1" || m.Contains(adbg)) Console.WriteLine("AREA " + m); };
        if (Environment.GetEnvironmentVariable("LOOPBOX") is { } lbx)
        {
            var bb = lbx.Split(',').Select(double.Parse).ToArray();
            Parametric3D.Kernel.Tess.ExactFaceMesher.RawLoopDebug = (face, uv) =>
            {
                var srf = face.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface fsx ? fsx.Source : face.Surface; if (srf == null) return;
                var pts = uv.Select(u => srf.PointAt(u)).Where(x => x.X >= bb[0] && x.X <= bb[1] && x.Y >= bb[2] && x.Y <= bb[3] && x.Z >= bb[4] && x.Z <= bb[5]).ToList();
                if (face.Tag.Contains("side[1]") || face.Tag.Contains("round:face")) Console.WriteLine($"LOOPSEEN {face.Id} {face.Tag} n={uv.Count} first {srf.PointAt(uv[0])} {srf.GetType().Name}");
                if (pts.Count > 0) Console.WriteLine($"LOOP {face.Id} {face.Tag} n={uv.Count}: " + string.Join(" ", pts.Select(x => $"({x.X:0.#####},{x.Y:0.#####},{x.Z:0.#####})")));
            };
        }
        bool prof = Environment.GetEnvironmentVariable("CASEPROF") == "1";
        if (prof) { Parametric3D.Kernel.Diagnostics.Prof.Enabled = true; Parametric3D.Kernel.Diagnostics.Prof.Reset(); }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        p.Rebuild(pars);
        Console.WriteLine($"FULL {sw.ElapsedMilliseconds} ms");
        if (prof) { Parametric3D.Kernel.Diagnostics.Prof.Enabled = false; Console.WriteLine(Parametric3D.Kernel.Diagnostics.Prof.Report()); }
        Parametric3D.Kernel.Ops.FilletOp.BuiltTool = null;
        if (Parametric3D.Kernel.Ops.BooleanOp.LastInvalidResult is { } bad)
        {
            var v = SolidValidator.ValidateBRep(bad);
            foreach (var iss in v.Issues.Where(i => i.Severity == ValidationSeverity.Error).Take(4))
            {
                Console.WriteLine("INVALID " + iss);
                if (iss.Element is uint id)
                {
                    if (bad.Faces.FirstOrDefault(x => x.Id == id) is { } fz0)
                        foreach (var l in fz0.Loops)
                        {
                            var bal = new Dictionary<TVertex, int>();
                            foreach (var ce in l.CoEdges) { bal[ce.Start] = bal.GetValueOrDefault(ce.Start) + 1; bal[ce.End] = bal.GetValueOrDefault(ce.End) - 1; }
                            foreach (var (vx, b) in bal.Where(kv => kv.Value != 0)) Console.WriteLine($"   UNBALANCED V{vx.Id} {vx.P:0.######} {b:+0;-0} edges here: " + string.Join(" ", l.CoEdges.Where(c => c.Start == vx || c.End == vx).Select(c => $"e{c.Edge.Id}:V{c.Start.Id}->V{c.End.Id} len {(c.End.P - c.Start.P).Length:E2}")));
                        }
                    if (bad.Faces.FirstOrDefault(x => x.Id == id) is { } fz)
                        foreach (var l in fz.Loops) Console.WriteLine($"   face {fz.Tag} loop: " + string.Join(" ", l.CoEdges.Select(ce => $"{ce.Edge.Curve?.GetType().Name[..3]}({ce.Start.P.X:0.##},{ce.Start.P.Y:0.##},{ce.Start.P.Z:0.##})->({ce.End.P.X:0.##},{ce.End.P.Y:0.##},{ce.End.P.Z:0.##})" + (l.CoEdges.Count <= 2 ? $"[V {ce.Edge.V0.Id}->{ce.Edge.V1.Id} len {(ce.Edge.V1.P - ce.Edge.V0.P).Length:E3} edge {ce.Edge.Id} uses {ce.Edge.CoEdges.Count}]" : "") + (l.CoEdges.Count == 1 ? $"[same vertex {ReferenceEquals(ce.Edge.V0, ce.Edge.V1)}, curve {ce.Edge.Curve?.PointAt(ce.Edge.Curve.T0)} → {ce.Edge.Curve?.PointAt(ce.Edge.Curve.T1)}, uses {ce.Edge.CoEdges.Count}: {string.Join(",", ce.Edge.CoEdges.Select(c2 => c2.Loop?.Face?.Tag))}]" : ""))));
                    if (bad.Edges.FirstOrDefault(x => x.Id == id) is { } ed)
                        Console.WriteLine($"   edge {ed.V0.P:0.####} → {ed.V1.P:0.####} {ed.Curve?.GetType().Name}; faces: {string.Join(" | ", ed.CoEdges.Select(c => c.Loop?.Face?.Tag))}");
                }
            }
        }
        var f = p.Features[^1];
        Console.WriteLine($"op {f.Summary}: {(f.Error ?? "ok").Replace('\n', '#')}");
        foreach (var w in p.Warnings.Where(x => x.Contains("Round") || x.Contains("Chamfer") || x.Contains("Boolean") || x.Contains("downgrade"))) Console.WriteLine("WARN " + w);
        Console.WriteLine($"bodies {p.Bodies.Count}: " + string.Join(", ", p.Bodies.Select(b => $"V {AnalyticVolume.Of(b)?.ToString("0.###") ?? Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(b)).ToString("0.###")} box {b.Bounds.Min:0.##} {b.Bounds.Max:0.##}")));
        if (p.Bodies.Count == 1)
        {
            WriteObj(outPrefix + "_after.obj", p.Bodies);
            var vr = SolidValidator.Validate(p.Bodies[0]);
            Console.WriteLine($"volume {v0:0.###} -> {p.Volume:0.###} (dv {v0 - p.Volume:0.####}), guarantee {p.Bodies[0].Guarantee}, valid {vr.IsValid}");
            foreach (var iss in vr.Issues.Take(6)) Console.WriteLine("ISSUE " + iss);
            if (Environment.GetEnvironmentVariable("NEARPT") is { } np)
            try {
                var c = np.Split(',').Select(double.Parse).ToArray(); var q = new OpenTK.Mathematics.Vector3d(c[0], c[1], c[2]);
                {
                    if (p.Bodies[0].Exact is { } exb)
                    {
                        var polys = Parametric3D.Kernel.Exact.ExactAdapter.ToCsgForDisplay(exb.Solid);
                        foreach (var cp in polys)
                        {
                            bool near = false;
                            for (int k = 1; k + 1 < cp.V.Count && !near; k++) near = TriDist(cp.V[0], cp.V[k], cp.V[k + 1], q) < 2e-4;
                            if (near) Console.WriteLine($"POLY tag {cp.Tag} n={cp.V.Count}: " + string.Join(" ", cp.V.Select(x => $"({x.X:0.#####},{x.Y:0.#####},{x.Z:0.#####})")));
                        }
                    }
                    var md = Parametric3D.Kernel.Tess.Tessellator.Tessellate(p.Bodies[0]); var seenF = new HashSet<TFace>();
                    if (Environment.GetEnvironmentVariable("MESHBOX") is { } mb)
                    {
                        var parts = mb.Split(':'); var bx = parts[0].Split(',').Select(double.Parse).ToArray(); uint want = uint.Parse(parts[1]);
                        var fr0 = md.FaceRanges[want];
                        if (p.Bodies[0].Faces.FirstOrDefault(ff => ff.Id == want) is { } wf)
                            Console.WriteLine($"BOXFACE {wf.Id} tag {wf.Tag} {wf.Surface?.GetType().Name} src {GeometryAudit.AuthoritativeSurface(wf)?.GetType().Name} facets {(wf.Surface as Parametric3D.Kernel.Surfaces.FacetedSurface)?.Facets.Count} reversed {wf.Reversed}");
                        for (int t = fr0.IndexStart; t < fr0.IndexStart + fr0.IndexCount; t += 3)
                        {
                            var ids = new[] { md.Indices[t], md.Indices[t + 1], md.Indices[t + 2] };
                            var P = ids.Select(ix => md.Vertices[ix].P).ToArray();
                            if (P.Any(x => x.X >= bx[0] && x.X <= bx[1] && x.Y >= bx[2] && x.Y <= bx[3] && x.Z >= bx[4] && x.Z <= bx[5]))
                                Console.WriteLine($"BOXTRI {string.Join(" ", ids.Zip(P, (i, x) => $"{i}:({x.X:0.#####},{x.Y:0.#####},{x.Z:0.#####})"))}");
                        }
                    }
                    foreach (var (fid, fr) in md.FaceRanges)
                        for (int t = fr.IndexStart; t < fr.IndexStart + fr.IndexCount && t + 2 < md.Indices.Length; t += 3)
                        {
                            if (md.Indices[t] >= md.Vertices.Length || md.Indices[t + 1] >= md.Vertices.Length || md.Indices[t + 2] >= md.Vertices.Length) { Console.WriteLine($"BADIDX face {fid}"); break; }
                            var P = new[] { md.Indices[t], md.Indices[t + 1], md.Indices[t + 2] }.Select(ix => new Vector3d(md.Vertices[ix].P.X, md.Vertices[ix].P.Y, md.Vertices[ix].P.Z)).ToArray();
                            if (P.Any(x => (x - q).Length < 0.003) && p.Bodies[0].Faces.FirstOrDefault(ff => ff.Id == fid) is { } bf && seenF.Add(bf))
                                Console.WriteLine($"MESHFACE {fid}: brep {bf.Id} tag {bf.Tag} {bf.Surface?.GetType().Name} src {GeometryAudit.AuthoritativeSurface(bf)?.GetType().Name} loops " + string.Join(" / ", bf.Loops.Select(lp => string.Join(" ", lp.CoEdges.Select(c => $"{c.Start.P:0.###}")))));
                            if (P.Any(x => (x - q).Length < 0.003) || TriDist(P[0], P[1], P[2], q) < 2e-4) Console.WriteLine($"TRI face {fid} " + string.Join(" ", P.Select(x => $"({x.X:0.#####},{x.Y:0.#####},{x.Z:0.#####})")) + $" area {Vector3d.Cross(P[1] - P[0], P[2] - P[0]).Length / 2:G3}");
                        }
                }
                var dumped = new HashSet<TFace>();
                foreach (var e in p.Bodies[0].Edges)
                {
                    if (e.Curve == null) continue; double best0 = SegDist(e, q);
                    if (best0 < 0.01) foreach (var ce in e.CoEdges) if (ce.Loop?.Face is { } ff && dumped.Add(ff))
                        foreach (var lp in ff.Loops) Console.WriteLine($"FACE {ff.Id} {ff.Surface?.GetType().Name} area? loop[{lp.CoEdges.Count}]: " + string.Join(" ", lp.CoEdges.Select(c => $"{c.Edge.Id}:{c.Start.P:0.###}->{c.End.P:0.###}")));
                }
                foreach (var e in p.Bodies[0].Edges)
                {
                    if (e.Curve == null) continue; double best = SegDist(e, q);
                    if (best > 0.3) continue;
                    Console.WriteLine($"NEAR edge {e.Id} {e.Curve.GetType().Name} d={best:0.####} len={(e.V1.P - e.V0.P).Length:0.####} {e.V0.P:0.####}->{e.V1.P:0.####} faces: " +
                        string.Join(" | ", e.CoEdges.Select(ce => $"{ce.Loop?.Face?.Id} {ce.Loop?.Face?.Surface?.GetType().Name} loops={ce.Loop?.Face?.Loops.Count} rev={ce.Reversed}")));
                }
            } catch (Exception ex) { Console.WriteLine("NEARPT failed: " + ex); }
        }
        if (tool != null) { WriteObj(outPrefix + "_tool.obj", new[] { tool }); Console.WriteLine($"tool volume {AnalyticVolume.Of(tool)?.ToString("0.###") ?? "?"} box {tool.Bounds.Min} {tool.Bounds.Max}"); }
        var (pp, _) = DocumentFile.Load(text);
        if (pp.Features[^1] is RoundFeature rf2) foreach (var er in rf2.Edges) Console.WriteLine("edge ref " + System.Text.Json.JsonSerializer.Serialize(er));
        if (pp.Features[^1] is ChamferFeature cf2) foreach (var er in cf2.Edges) Console.WriteLine("edge ref " + System.Text.Json.JsonSerializer.Serialize(er));
    }
}

static partial class Program
{
    /// <summary>
    /// k140: ПОСЛЕДОВАТЕЛЬНЫЕ ПРАВКИ на моделях корпуса — как в сессии пользователя: на каждой модели steps скруглений и фасок
    /// случайных острых рёбер (с цепочкой), каждая — поверх предыдущих. Замеры те же: отказ, годность, объём против сечения
    /// по граням (для рёбер, не задетых прежними правками), щепки, время.
    /// </summary>
    static void LoftCorpusSeq(int count, int seed0, int steps, string outDir)
    {
        Directory.CreateDirectory(outDir);
        int ok = 0, bad = 0, refused = 0, legit = 0; var why = new Dictionary<string, int>(); double totalMs = 0;
        for (int sd = seed0; sd < seed0 + count; sd++)
        {
            CorpusModel m;
            try { m = CorpusMake(sd); m.Part.Rebuild(null); } catch (Exception ex) { Log(new { seed = sd, loft = "exception " + ex.Message }); continue; }
            if (m.Part.Features[0].Error != null || m.Part.Bodies.Count != 1) { Log(new { seed = sd, loft = "refused" }); continue; }
            var p = m.Part; var rnd = new Random(sd * 31 + 7);
            for (int st = 0; st < steps; st++)
            {
                var edges = p.Bodies[0].Edges.Where(e => e.Curve != null && Program.Sharp(e) && SharpEverywhere(e) && EdgeLen(e) > 2).ToList();
                if (edges.Count == 0) break;
                var e = edges[rnd.Next(edges.Count)];
                var chain = CorpusChain(e, p.Bodies[0].Edges.ToList());
                double minLen = chain.Min(ce => { var c = ce.Curve!; double l = 0; var pr = c.PointAt(c.T0); for (int i = 1; i <= 16; i++) { var q = c.PointAt(c.T0 + (c.T1 - c.T0) * i / 16.0); l += (q - pr).Length; pr = q; } return l; });
                bool round = rnd.Next(2) == 0;
                double size = Math.Round(Math.Clamp(minLen * (0.05 + 0.15 * rnd.NextDouble()), 0.3, 6), 2);
                double? expect = 0; foreach (var ce in chain) { var x = CorpusExpected(ce, round, size); expect = x == null || expect == null ? null : expect + x; }
                var before = TopologyClone.Deep(p.Bodies[0]); double v0 = p.Volume;
                Feature f = round ? new RoundFeature($"R{st}") { Radius = new ValueRef(size) } : new ChamferFeature($"C{st}") { D1 = new ValueRef(size), D2 = new ValueRef(size) };
                foreach (var ce in chain) { if (f is RoundFeature rf) rf.Edges.Add(EdgeRef.Of(ce)); else ((ChamferFeature)f).Edges.Add(EdgeRef.Of(ce)); }
                p.AddFeature(f);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                // k143: путь операции — прямой построитель (замена граней, как Parasolid) или резец; причина отказа прямого
                string? drWhy = null; var drPrev = Parametric3D.Kernel.Ops.DirectRound.Debug;
                Parametric3D.Kernel.Ops.DirectRound.Debug = s => { if (Environment.GetEnvironmentVariable("LOFT_DRDEBUG") is { } dd && s.Contains(dd)) Console.WriteLine("DBG " + (s.Length > 300 ? s[..300] : s)); if (s.StartsWith("declined")) drWhy = s.Length > 200 ? s[..200] : s; };
                try { p.Rebuild(null); } catch (Exception ex) when (ex is not OutOfMemoryException) { f.Error = "exception: " + ex.Message; }
                finally { Parametric3D.Kernel.Ops.DirectRound.Debug = drPrev; }
                double ms = sw.ElapsedMilliseconds; totalMs += ms;
                var mid = e.Curve!.PointAt(0.5 * (e.Curve.T0 + e.Curve.T1));
                string tag = $"s{sd} step {st} {(round ? "R" : "C")}{size} edge ({mid.X:0.##},{mid.Y:0.##},{mid.Z:0.##}){(chain.Count > 1 ? $" chain {chain.Count}" : "")}";
                var problems = new List<string>();
                if (f.Error != null && LegitRefusal(f.Error)) { legit++; Log(new { tag, ms, legit = f.Error.Split('\n').Last(), full = f.Error }); try { File.WriteAllText(Path.Combine(outDir, $"legit_s{sd}_{st}.p3d"), DocumentFile.Save(p)); } catch { } p.RemoveFeature(f); p.Rebuild(null); continue; }
                if (f.Error != null) { refused++; problems.Add("refused: " + f.Error.Split('\n').Last()); }
                else if (p.Bodies.Count != 1) problems.Add($"the body split into {p.Bodies.Count} bodies");
                else
                {
                    var b = p.Bodies[0]; var vr = SolidValidator.Validate(b);
                    if (!vr.IsValid) problems.Add("invalid: " + string.Join(" / ", vr.Issues.Where(i => i.Severity == ValidationSeverity.Error).Take(2)));
                    static double MeshVol(TSolid t) => Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(t));
                    double dv = b.Guarantee.ToString() == "ExactAnalytic" && before.Guarantee.ToString() == "ExactAnalytic" ? Math.Abs(p.Volume - v0) : Math.Abs(MeshVol(b) - MeshVol(before));
                    // эталон — только у ребра между гранями лофта (без прежних правок по соседству)
                    bool clean = chain.All(ce => ce.CoEdges.All(c => c.Loop?.Face?.Tag is { } tg && tg.Contains("loft:")));
                    bool closedChain = chain.All(ce => chain.Count(o => (o.V0.P - ce.V0.P).Length < 1e-7 || (o.V1.P - ce.V0.P).Length < 1e-7) >= 2 && chain.Count(o => (o.V0.P - ce.V1.P).Length < 1e-7 || (o.V1.P - ce.V1.P).Length < 1e-7) >= 2);
                    if (clean && expect is { } ex && double.IsFinite(ex))
                    {
                        double tol = 0.03 * ex + (closedChain ? 0 : 2.5 * size * size * size) + 0.02
                                    // гранение граней (допуск хорды) против точного сечения: полоса шириной хорды на половине ширины касания вдоль цепочки
                                    + 0.5 * Parametric3D.Core.Tol.Chord * size * chain.Sum(EdgeLen);
                        if (Math.Abs(dv - ex) > tol) problems.Add($"volume change {dv:0.###} vs formula {ex:0.###}");
                    }
                    if (dv < 1e-6) problems.Add("nothing changed");
                    Log(new { tag, ms, guarantee = b.Guarantee.ToString(), dv = Math.Round(dv, 4), expect = clean && expect is { } e2 ? Math.Round(e2, 4) : (double?)null, problems, dr = drWhy });
                }
                if (f.Error != null || p.Bodies.Count != 1) Log(new { tag, ms, problems });
                if (Environment.GetEnvironmentVariable("CORPUS_SAVEALL") == "1")
                    try { File.WriteAllText(Path.Combine(outDir, $"all_s{sd}_{st}.p3d"), DocumentFile.Save(p)); } catch { }
                if (problems.Count == 0) { ok++; continue; }
                if (f.Error == null) bad++;
                foreach (var pr in problems)
                {
                    string key = System.Text.RegularExpressions.Regex.Replace(pr, @"[-0-9]+\.[0-9]+(E[-+0-9]+)?", "#"); if (key.Length > 140) key = key[..140];
                    why[key] = why.GetValueOrDefault(key) + 1;
                }
                try { File.WriteAllText(Path.Combine(outDir, $"seq_s{sd}_{st}.p3d"), DocumentFile.Save(p)); } catch { }
                // отказ — правка убирается, сессия продолжается
                if (f.Error != null || p.Bodies.Count != 1) { p.RemoveFeature(f); p.Rebuild(null); }
            }
        }
        Log(new { ok, bad, refused, legit, totalOpSeconds = Math.Round(totalMs / 1000, 1) });
        foreach (var kv in why.OrderByDescending(k => k.Value)) Console.WriteLine($"{kv.Value,4}  {kv.Key}");
    }
}
