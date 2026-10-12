using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using OpenTK.Mathematics;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;

static partial class Program
{
    // ПОСТАВКА 3. ОПЫТ ПЕРЕД ПЕРЕИСПОЛЬЗОВАНИЕМ СЕТКИ ГРАНЕЙ: зависит ли окончательная видимая сетка грани ТОЛЬКО
    // от её точного ключа? Ключ — имя, обход, ИСХОДНАЯ ПОВЕРХНОСТЬ ПО ССЫЛКЕ, биты треугольников грани, биты
    // рёбер (концы, точки кривой) и ОБРАЗЫ ВЕРШИН ПОСЛЕ ПРИВЯЗКИ ПО ВСЕМУ ТЕЛУ, настройки точности. Если ключ уже
    // встречался, свежая сетка сравнивается со старой побитово. Переиспользование оправдано только при нуле
    // расхождений на всём наборе правок.
    static readonly ConditionalWeakTable<object, object> RefIds = new();
    static long _refNext;
    static long RefId(object o) => (long)RefIds.GetValue(o, _ => (object)Interlocked.Increment(ref _refNext));

    static string FaceKey(TFace f, Func<Vector3d, Vector3d>? snap)
    {
        using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
        w.Write(Parametric3D.Core.Tol.Chord); w.Write(Parametric3D.Core.Tol.ChordAngle);
        w.Write(f.Tag); w.Write(f.Reversed);
        var fs = (FacetedSurface)f.Surface;
        w.Write(fs.Source == null ? -1L : RefId(fs.Source));
        void P(Vector3d p) { w.Write(p.X); w.Write(p.Y); w.Write(p.Z); }
        w.Write(fs.Facets.Count); foreach (var (a, b, c) in fs.Facets) { P(a); P(b); P(c); }
        foreach (var l in f.Loops)
        {
            w.Write(l.IsOuter); w.Write(l.CoEdges.Count);
            foreach (var ce in l.CoEdges)
            {
                var e = ce.Edge; w.Write(ce.Reversed); w.Write(e.Curve.GetType().Name);
                for (int i = 0; i <= 8; i++) P(e.Curve.PointAt(e.Curve.T0 + (e.Curve.T1 - e.Curve.T0) * i / 8));
                P(e.V0.P); P(e.V1.P);
                if (snap != null) { P(snap(e.V0.P)); P(snap(e.V1.P)); }
            }
        }
        w.Flush();
        return Convert.ToHexString(SHA256.HashData(ms.ToArray()));
    }

    static string MeshBits(TFace f)
    {
        var fs = (FacetedSurface)f.Surface;
        using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
        void P(Vector3d p) { w.Write(p.X); w.Write(p.Y); w.Write(p.Z); }
        var d = fs.Display; w.Write(d?.Count ?? -1);
        if (d != null) foreach (var (a, b, c) in d) { P(a); P(b); P(c); }
        var n = fs.DisplayNormals; w.Write(n?.Count ?? -1);
        if (n != null) foreach (var (a, b, c) in n) { P(a); P(b); P(c); }
        w.Flush();
        return Convert.ToHexString(SHA256.HashData(ms.ToArray()));
    }

    static void MeshReuseExperiment()
    {
        // Опыт — при ВЫКЛЮЧЕННОМ переиспользовании: каждая сетка считается заново, иначе совпадения были бы тавтологией.
        using var noReuse = new Parametric3D.Kernel.KernelContext(Parametric3D.Kernel.KernelSettings.Capture() with { ReuseFaceMeshes = false }).Enter();
        var seen = new Dictionary<string, string>();          // ключ грани → биты её сетки
        int faces = 0, reusable = 0, match = 0, mismatch = 0, rebuilds = 0;
        var mismatches = new List<string>();
        foreach (int seed in new[] { 8001, 8002, 8003, 8004 })
        {
            var p = RealisticPart(seed, 20, out _); p.Rebuild(null);
            var prevKeys = new HashSet<string>();
            void Record(bool compare)
            {
                foreach (var b in p.Bodies)
                {
                    if (!Snaps.TryGetValue(b, out var snap)) continue;          // тело не размечалось заново
                    foreach (var f in b.Faces)
                    {
                        if (f.Surface is not FacetedSurface) continue;
                        string k = FaceKey(f, snap), m = MeshBits(f); faces++;
                        if (compare && prevKeys.Contains(k)) reusable++;
                        if (seen.TryGetValue(k, out var old)) { if (old == m) match++; else { mismatch++; if (mismatches.Count < 6) mismatches.Add($"seed {seed} face {f.Tag}"); } }
                        else seen[k] = m;
                    }
                }
                prevKeys = new HashSet<string>();
                foreach (var b in p.Bodies)
                    if (Snaps.TryGetValue(b, out var sn))
                        foreach (var x in b.Faces) if (x.Surface is FacetedSurface) prevKeys.Add(FaceKey(x, sn));
            }
            Snaps.Clear();
            BooleanOp.MeshSnapProbe = (s, snap) => Snaps.AddOrUpdate(s, snap);
            try
            {
                p.Invalidate(); p.Rebuild(null); rebuilds++; Record(false);
                // Правки: диаметр каждого отверстия и глубина каждого кармана — по одной, с перестроением с этой операции.
                foreach (var feat in p.Features.ToList())
                {
                    if (feat is HoleFeature h) h.Diameter = new ValueRef(h.Diameter.Value * 1.07);
                    else if (feat is ExtrudeFeature ex && ex.Cut) ex.Side1.Distance = new ValueRef(ex.Side1.Distance.Value + 0.7);
                    else continue;
                    Snaps.Clear();
                    p.MarkDirtyFrom(feat); p.Rebuild(null); rebuilds++; Record(true);
                }
            }
            finally { BooleanOp.MeshSnapProbe = null; }
        }
        Log(new { test = "MESH-REUSE-EXPERIMENT", rebuilds, facesMeshed = faces, reusableFromPrevious = reusable, keyRepeats = match + mismatch, match, mismatch, mismatches });
        Check("MESH-REUSE-EXPERIMENT", () => Require(mismatch == 0, $"{mismatch} face(s) with the same exact key got a different mesh: {string.Join("; ", mismatches)}"));
    }
    static readonly ConditionalWeakTable<TSolid, Func<Vector3d, Vector3d>?> Snaps = new();

    /// <summary>
    /// Поставка 3: переиспользование сырой сетки граней (BooleanOp.ReuseFaceMeshes) не меняет НИЧЕГО: та же
    /// последовательность правок с переиспользованием и без — побитово те же сетки граней и тел и объёмы.
    /// </summary>
    static void MeshReuseIdentity()
    {
        Check("G7-MESH-REUSE-IDENTICAL", () =>
        {
            int analyticDonors0 = Parametric3D.Kernel.Export.StepImport.DonorReused;
            int reused = 0, edits = 0, facesCompared = 0;
            foreach (int seed in new[] { 8001, 8002, 8003, 8004 })
            {
                var on = RealisticPart(seed, 20, out _); var off = RealisticPart(seed, 20, out _);
                void Both(Action<Part> edit)
                {
                    edit(on); on.Rebuild(null); reused += on.LastMeshFacesReused;
                    using (new Parametric3D.Kernel.KernelContext(Parametric3D.Kernel.KernelSettings.Capture() with { ReuseFaceMeshes = false }).Enter())
                    { edit(off); off.Rebuild(null); }
                    Require(off.LastMeshFacesReused == 0, "reuse switched off but faces were reused");
                    Require(on.Bodies.Count == off.Bodies.Count, "body count differs");
                    for (int i = 0; i < on.Bodies.Count; i++)
                    {
                        var fa = on.Bodies[i].Faces.ToList(); var fb = off.Bodies[i].Faces.ToList();
                        Require(fa.Count == fb.Count, "face count differs");
                        for (int k = 0; k < fa.Count; k++)
                            if (fa[k].Surface is FacetedSurface)
                            { facesCompared++; Require(MeshBits(fa[k]) == MeshBits(fb[k]), $"seed {seed}: face {fa[k].Tag} mesh differs with reuse"); }
                        Require(SolidFingerprint.Geometry(on.Bodies[i]) == SolidFingerprint.Geometry(off.Bodies[i]), $"seed {seed}: body geometry differs");
                    }
                    Require(on.Volume == off.Volume && on.Integrity == off.Integrity, $"seed {seed}: volume/integrity differ");
                }
                Both(p => { });
                foreach (int fi in Enumerable.Range(0, on.Features.Count))
                {
                    if (on.Features[fi] is HoleFeature) { edits++; Both(p => { var h = (HoleFeature)p.Features[fi]; h.Diameter = new ValueRef(h.Diameter.Value * 1.07); p.MarkDirtyFrom(h); }); }
                    else if (on.Features[fi] is ExtrudeFeature { Cut: true }) { edits++; Both(p => { var x = (ExtrudeFeature)p.Features[fi]; x.Side1.Distance = new ValueRef(x.Side1.Distance.Value + 0.7); p.MarkDirtyFrom(x); }); }
                }
            }
            Log(new { test = "G7-MESH-REUSE-IDENTICAL", edits, facesCompared, facesReused = reused,
                      analyticDonors = Parametric3D.Kernel.Export.StepImport.DonorReused - analyticDonors0 });
            Require(reused > 0, "no face mesh was reused on edits");
        });
    }

    /// <summary>
    /// Поставка 3: история операции сравнивает области ТОЧНО. Надрез шириной 3e-5 мм (три шага сетки) и высотой 2 мм в
    /// боковой грани меняет её площадь на 6e-5 мм² — меньше прежнего допуска (1e-7 × площадь ≈ 1.2e-4): сравнение по
    /// площади и центру считало бы грань прежней, точное обязано увидеть изменение. И наоборот: удалённый резец
    /// грань не меняет — «прежняя» без допуска.
    /// </summary>
    static void HistoryExact()
    {
        Check("G7-HISTORY-EXACT", () =>
        {
            var w = new List<string>();
            var a = new List<TSolid> { Box() }; BooleanOp.Subtract(a, Box(200, 200, 200, 1, 1, 1), w);   // точное описание, грани целы
            // Грани резца — со своими именами (в дереве детали их даёт ключ операции; здесь иначе они совпали бы с
            // именами граней бруска, и стенки прорези попали бы в те же группы).
            var notch = Box(59, 10, 5, 2, 0.00003, 2); foreach (var f in notch.Faces) f.Tag = "notch/" + f.Tag;
            var b = new List<TSolid> { Box() }; BooleanOp.Subtract(b, notch, w);   // надрез шириной 3e-5 мм (три шага сетки) вдоль грани x = 60
            var c = new List<TSolid> { Box() }; BooleanOp.Subtract(c, Box(300, 300, 300, 1, 1, 1), w);
            // Точная форма — готовая или ленивая (аналитический маршрут строит её по требованию).
            static bool HasExact(TSolid t) => t.Exact != null || t.LazyDerivedExact != null;
            Require(HasExact(a[0]) && HasExact(b[0]) && HasExact(c[0]), "exact form expected");
            string? tolSaid = null;
            FeatureHistory.CompareDebug = m => { if (m.StartsWith("DISAGREE")) tolSaid = m; };
            try
            {
                var hb = FeatureHistory.Between(a, b);
                var hc = FeatureHistory.Between(a, c);
                Log(new { test = "G7-HISTORY-EXACT", modified = hb.Modified, split = hb.Split, generated = hb.Generated, unchangedModified = hc.Modified, toleranceDisagreement = tolSaid });
                Require(hb.Modified.SequenceEqual(new[] { "extrude[0]:side[0.1]" }), "exactly the notched face x = 60 must be modified: " + string.Join(",", hb.Modified));
                Require(hb.Split.Count == 0 && hb.Deleted.Count == 0, $"untouched faces reported as split/deleted: {string.Join(",", hb.Split)} | {string.Join(",", hb.Deleted)}");
                Require(hb.Generated.Count > 0 && hb.Generated.All(t => t.StartsWith("notch/")), "notch walls must be generated: " + string.Join(",", hb.Generated));
                Require(tolSaid != null && tolSaid.Contains("tolerance True, exact False"), "the tolerance comparison was expected to miss the notch: " + tolSaid);
                Require(hc.Modified.Count == 0 && hc.Split.Count == 0, "untouched faces reported as modified: " + string.Join(",", hc.Modified));
            }
            finally { FeatureHistory.CompareDebug = null; }
        });
    }
}
