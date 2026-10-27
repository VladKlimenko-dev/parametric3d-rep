using OpenTK.Mathematics;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.MeshFit;

/// <summary>Как переводить сетку в тело.</summary>
public sealed class MeshFitOptions
{
    /// <summary>Точная геометрия: плоскости, цилиндры, конусы, сферы, торы. false — только плоские грани (кривое — гранями).</summary>
    public bool Exact = true;
    /// <summary>Множитель допуска (1 — сетка из САПР; скан — 10…100).</summary>
    public double TolScale = 1;
    /// <summary>Выравнивание по замыслу: оси к осям координат, соосность, круглые значения.</summary>
    public bool Snap = true;
    /// <summary>Масштаб координат файла (единицы STL → мм).</summary>
    public double Scale = 1;
}

/// <summary>Итог перевода сетки в тело.</summary>
public sealed class MeshFitResult
{
    public string? Step;
    public TSolid? Body;
    public bool Exact;
    public int Triangles, Regions, Planes, Cylinders, Cones, Spheres, Tori;
    public int Lines, Circles, Ellipses, Polylines;
    public int DirSnaps, Coaxial, ValueSnaps, Tangents, TolerantEdges, Leashes;
    public double MeshVolume, BodyVolume, Tolerance, Ms;
    public readonly List<string> Notes = new();
    public string Summary => Body == null ? "не удалось: " + string.Join("; ", Notes)
        : $"{(Exact ? "точное тело" : "тело из плоских граней")}: граней {Regions} (плоскостей {Planes}, цилиндров {Cylinders}, конусов {Cones}, сфер {Spheres}, торов {Tori}); " +
          $"рёбра: прямых {Lines}, окружностей {Circles}, эллипсов {Ellipses}, ломаных {Polylines}; выравниваний {DirSnaps + Coaxial + ValueSnaps + Tangents}; {Ms:0} мс";
}

/// <summary>
/// СЕТКА → ТОЧНОЕ ТЕЛО (k119). Цепочка: подготовка сетки (MeshPrep) → области с поверхностями (Segmenter) →
/// выравнивание по замыслу (Regularizer) → вершины, рёбра, текст STEP (BRepFromRegions) → тело тем же импортом STEP →
/// проверка (тело годно, объём близок к объёму сетки). Не вышло точно — тело из плоских граней; не вышло и так — отказ
/// с причиной.
/// </summary>
public static class MeshToBrep
{
    [ThreadStatic] public static Action<string>? Debug;
    public static readonly bool Off = Environment.GetEnvironmentVariable("P3D_MESHFIT") == "0";

    public static MeshFitResult Convert(IReadOnlyList<(Vector3d A, Vector3d B, Vector3d C)> tris0, MeshFitOptions opt, string name = "mesh", Func<bool>? stop = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tris = opt.Scale == 1 ? tris0 : tris0.Select(t => (t.A * opt.Scale, t.B * opt.Scale, t.C * opt.Scale)).ToList();
        var res = new MeshFitResult { Triangles = tris.Count };
        PreparedMesh.Debug = Debug;
        var mesh = PreparedMesh.Prepare(tris);
        Debug?.Invoke($"prepare {sw.ElapsedMilliseconds} ms: {mesh.TriCount} tris, {mesh.V.Length} verts");
        if (mesh.TriCount < 4) { res.Notes.Add("в файле нет треугольников"); return res; }
        if (!mesh.Closed) res.Notes.Add($"сетка не замкнута: открытых сторон {mesh.BorderSides}, неоднозначных {mesh.NonManifold}");
        for (int t = 0; t < mesh.TriCount; t++)
            res.MeshVolume += Vector3d.Dot(mesh.P(t, 0), Vector3d.Cross(mesh.P(t, 1), mesh.P(t, 2))) / 6;
        double tol = Math.Max(1e-7, mesh.Diag * 1e-5) * Math.Max(opt.TolScale, 1e-3);
        res.Tolerance = tol;

        // попытки: точно → плоскими гранями с заданным допуском → плоскими гранями строго (одна плоскость — один кусок; с
        // большим допуском у сетки-скана плоские куски соседей могут сойтись в щель, строго — нет)
        var modes = new List<(bool Exact, bool Strict)>();
        if (opt.Exact && !Off) modes.Add((true, false));
        modes.Add((false, false));
        if (opt.TolScale > 1) modes.Add((false, true));
        foreach (var (exact, strict) in modes)
        {
            try
            {
                // без преобразования — плоскими гранями: в одну грань идут только треугольники одной плоскости (точность
                // координат файла), чтобы рёбра мозаики оставались по рёбрам сетки
                // (при заданном большем допуске — сетка-скан — и плоские грани собираются с ним)
                double tolHere = exact || opt.TolScale > 1 && !strict ? tol : Math.Min(tol, Math.Max(1e-7, mesh.Diag * 1e-6));
                var seg = new Segmenter(mesh, tolHere) { PlanesOnly = !exact };
                seg.Trace2 = Debug;
                seg.Run(stop);
                Debug?.Invoke($"segment {sw.ElapsedMilliseconds} ms");
                var regs = seg.Regions;
                var reg = new Regularizer(mesh, regs, tolHere);
                if (opt.Snap && exact) reg.Run();
                Debug?.Invoke($"regularize {sw.ElapsedMilliseconds} ms");
                Debug?.Invoke($"{(exact ? "exact" : "flat")}: closed splits {seg.ClosedSplits}, merges {seg.Merges}, absorbed {seg.Absorbed}, tris/region {string.Join(",", regs.Take(8).Select(x => x.Tris.Count))}");
                Debug?.Invoke($"{(exact ? "exact" : "flat")}: regions {regs.Count}: " + string.Join("; ", regs.GroupBy(x => x.S.ToString() + (x.Sense < 0 ? " in" : "")).OrderByDescending(g => g.Count()).Take(12).Select(g => g.Count() + "×" + g.Key)));
                var br = new BRepFromRegions(mesh, regs, tolHere);
                string? step = br.Build(name);
                Debug?.Invoke($"brep {sw.ElapsedMilliseconds} ms");
                if (step == null) { res.Notes.Add((exact ? "точное: " : "плоское: ") + br.Problem); continue; }
                if (Debug != null) foreach (var ln in br.LeashNotes.Take(10)) Debug("  leash: " + ln);
                if (Debug != null) foreach (var ln in reg.Notes.Take(10)) Debug("  reg: " + ln);
                var warn = new List<string>();
                if (Debug != null) { File.WriteAllText(Path.Combine(Path.GetTempPath(), $"meshfit_{string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? c : (char)95))}_{(exact ? "x" : "f")}.step"), step); }
                var solids = Export.StepImport.ReadExactSolids(step, warn);
                if (solids.Count != 1) { res.Notes.Add((exact ? "точное" : "плоское") + $": тел {solids.Count}"); continue; }
                var body = solids[0];
                Debug?.Invoke($"import {sw.ElapsedMilliseconds} ms");
                if (!TolerantOff) res.TolerantEdges = SetTolerances(body, regs, tolHere);
                var val = SolidValidator.ValidateBRep(body);
                Debug?.Invoke($"validate {sw.ElapsedMilliseconds} ms");
                double vol = VolumeOf(body);
                bool volOk = mesh.Closed ? Math.Abs(vol - res.MeshVolume) <= 0.06 * Math.Abs(res.MeshVolume) : true;
                if (!val.IsValid || !volOk)
                {
                    Debug?.Invoke("  issues: " + string.Join(" | ", val.Issues.Where(x => x.Severity == ValidationSeverity.Error).Take(8).Select(x => x.ToString())));
                    res.Notes.Add((exact ? "точное" : "плоское") + $": тело {(val.IsValid ? "годно" : "негодно (" + val.Errors + ": " + string.Join(", ", val.Issues.Where(x => x.Severity == ValidationSeverity.Error).GroupBy(x => x.Code).Select(g => g.Key + "×" + g.Count())) + ")")}, объём {vol:0.###} при сетке {res.MeshVolume:0.###}");
                    continue;
                }
                res.Step = step; res.Body = body; res.Exact = exact; res.BodyVolume = vol;
                res.Regions = regs.Count;
                res.Planes = regs.Count(r => r.S.Kind == FitKind.Plane); res.Cylinders = regs.Count(r => r.S.Kind == FitKind.Cylinder);
                res.Cones = regs.Count(r => r.S.Kind == FitKind.Cone); res.Spheres = regs.Count(r => r.S.Kind == FitKind.Sphere); res.Tori = regs.Count(r => r.S.Kind == FitKind.Torus);
                res.Leashes = br.LeashFallbacks;
                res.Lines = br.Lines; res.Circles = br.Circles; res.Ellipses = br.Ellipses; res.Polylines = br.Polylines;
                res.DirSnaps = reg.DirSnaps; res.Coaxial = reg.Coaxial; res.ValueSnaps = reg.ValueSnaps; res.Tangents = reg.Tangents;
                LastRegions = regs; LastMesh = mesh;
                break;
            }
            catch (Exception ex) { res.Notes.Add((exact ? "точное: " : "плоское: ") + ex.Message); }
        }
        res.Ms = sw.Elapsed.TotalMilliseconds;
        return res;
    }

    public static readonly bool TolerantOff = Environment.GetEnvironmentVariable("P3D_MESHFITTOL") == "0";

    /// <summary>
    /// ДОПУСКИ РЁБЕР И ВЕРШИН — ИЗМЕРЕННЫЕ. Поверхности подогнаны к сетке с допуском, и там, где грани сходятся вскользь
    /// (почти параллельные плоскости мозаики, касание, не уточнённое до конца), ребро лежит на соседней грани не точнее
    /// этого допуска. Как в промышленных ядрах («tolerant edges»), ребро несёт свой допуск — измеренное наибольшее отклонение
    /// кривой от обеих поверхностей (не больше допуска подгонки); проверка тела спрашивает ребро. Мутация P3D_MESHFITTOL=0.
    /// </summary>
    static int SetTolerances(TSolid body, List<FitRegion> regs, double tol)
    {
        FitSurface? Of(TFace f)
        {
            var tag = f.Tag ?? "";
            int a = tag.IndexOf('['), z = tag.IndexOf(']');
            return a >= 0 && z > a && int.TryParse(tag.AsSpan(a + 1, z - a - 1), out int i) && i >= 0 && i < regs.Count ? regs[i].S : null;
        }
        int n = 0;
        foreach (var e in body.Edges)
        {
            var ss = e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Select(Of).OfType<FitSurface>().Distinct().ToList();
            if (ss.Count == 0) continue;
            double dev = 0;
            for (int k = 0; k <= 16; k++)
            {
                var p = e.Curve.PointAt(e.Curve.T0 + (e.Curve.T1 - e.Curve.T0) * k / 16.0);
                foreach (var s in ss) dev = Math.Max(dev, Math.Abs(s.Distance(p)));
            }
            if (dev > 0.5e-4 && dev <= tol * 1.5 && dev * 1.2 > e.Tolerance) { e.Tolerance = dev * 1.2; n++; }
            foreach (var v in new[] { e.V0, e.V1 })
            {
                double dv = ss.Max(s => Math.Abs(s.Distance(v.P)));
                if (dv > v.Tolerance && dv <= tol * 1.5) v.Tolerance = dv * 1.2;
            }
        }
        return n;
    }

    /// <summary>Объём тела: точный, иначе — по разметке.</summary>
    public static double VolumeOf(TSolid body)
    {
        try
        {
            if (AnalyticVolume.Of(body) is double v) return v;
            var copy = body;
            if (body.TopologyConvention < TopologyConventions.Version) { copy = TopologyClone.Deep(body); TopologyConventions.Normalize(copy); }
            return AnalyticVolume.Of(copy) ?? Tess.MassProps.Volume(Tess.Tessellator.Tessellate(copy));
        }
        catch { return double.NaN; }
    }

    /// <summary>Разбор: области последнего удачного перевода (проверки).</summary>
    [ThreadStatic] public static List<FitRegion>? LastRegions;
    [ThreadStatic] public static PreparedMesh? LastMesh;
}
