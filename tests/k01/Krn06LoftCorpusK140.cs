using Parametric3D.Kernel;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;

static partial class Program
{
    /// <summary>
    /// k140 (замечание 30.10, 07:01: «десятки подобных моделей… до 100%»): КОРПУС ЛОФТОВ — ЗАКРЕПЛЁННЫЕ СЛУЧАИ. Каждый файл
    /// tests/ui/data/corpus_k140/*.p3d — лофт корпуса (LoftCorpus: 2–3 сечения, многоугольники 3–8 сторон, прямоугольники,
    /// окружности, неправильные, отверстия, гладкие и линейчатые) с последней операцией — скруглением или фаской острого ребра
    /// (с касательной цепочкой, как AutoChain), на которой прежде был отказ, негодное тело, щепки или лишнее тело. Норма:
    /// операция принята, тело одно и годное (B-rep, точная форма, сетка показа), граней-щепок нет (площадь меньше 10⁻⁴ r² или
    /// средняя ширина меньше 0.005 r), снятый объём — как в манифесте (замер k140 против сечения шара по граням; 1% + 0.05 мм³).
    /// Мутации (каждая роняет хотя бы один случай): P3D_SLITLOOPS, P3D_MICROLOOP4, P3D_ISLANDS, P3D_TRACEEXACT,
    /// P3D_TANGENTINSET, P3D_ZEROAFTERHEAL, P3D_LINEFACET, P3D_SLIVERPIECES, P3D_SLIVERSTRIPS, P3D_ARCONSURF, P3D_BLENDCRUMBS,
    /// P3D_DROPTINYCAVITY (см. KRN06-LOFT-CORPUS-K140-MUT в отчёте k140); k140 — P3D_REGIONNEAREST (seq_s105_4), P3D_PATCHFOLD
    /// (seq_s106_2); k141 — P3D_FACESIDE (seq_s108_2), P3D_REACHFACERAYS (fail_s71), P3D_WALLCAPMIN (fail_s58);
    /// k142 — P3D_NORMALCONTINUITY (seq_s106_4: фаска 0.3 цепочки из 4, прежде «both faces must be planar»);
    /// k143 — P3D_ENDTRIM (seq_s101_4, seq_s102_2, seq_s102_5, seq_s110_5: прежние законные отказы — складка резца у конца).
    /// </summary>
    static void Krn06LoftCorpusK140Checks()
    {
        Check("KRN06-LOFT-CORPUS-K140", () =>
        {
            var dir = UserModelsDir() ?? throw new Exception("tests/ui/data not found");
            var cdir = Path.Combine(dir, "corpus_k140");
            var manifest = File.ReadAllLines(Path.Combine(cdir, "manifest.tsv")).Where(l => l.Length > 0 && !l.StartsWith("#"))
                               .Select(l => l.Split('\t')).ToList();
            var only = Environment.GetEnvironmentVariable("CORPUS_ONLY");
            var fails = new List<string>();
            int n = 0;
            foreach (var row in manifest)
            {
                string file = row[0]; double want = double.Parse(row[1], System.Globalization.CultureInfo.InvariantCulture);
                if (only != null && !file.Contains(only)) continue;
                n++;
                var r = CorpusFixture(Path.Combine(cdir, file));
                Log(new { file, r.Ok, dv = Math.Round(r.Dv, 4), want, r.Ms, r.Problems });
                foreach (var pr in r.Problems) fails.Add($"{file}: {pr}");
                if (r.Ok && Math.Abs(r.Dv - want) > 0.01 * Math.Abs(want) + 0.05) fails.Add($"{file}: removed volume {r.Dv:0.####}, expected {want:0.####}");
            }
            Require(n > 0, "no corpus cases");
            Require(fails.Count == 0, string.Join(" | ", fails.Take(8)));
        });
    }

    /// <summary>Случай корпуса: последняя операция файла — над телом без неё; замеры как в LoftCorpusRun.</summary>
    static (bool Ok, double Dv, long Ms, List<string> Problems) CorpusFixture(string file)
    {
        var problems = new List<string>();
        string text = File.ReadAllText(file);
        var (p0, pars0) = DocumentFile.Load(text);
        var last = p0.Features[^1];
        p0.RemoveFeature(last);
        p0.Rebuild(pars0);
        var before = TopologyClone.Deep(p0.Bodies.Single());
        double v0 = p0.Volume;
        double size = last switch { RoundFeature rf => rf.Radius.Value, ChamferFeature cf => cf.D1.Value, _ => 1 };
        var (p, pars) = DocumentFile.Load(text);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { p.Rebuild(pars); } catch (Exception ex) when (ex is not OutOfMemoryException) { problems.Add("exception: " + ex.Message); }
        long ms = sw.ElapsedMilliseconds;
        var f = p.Features[^1];
        if (f.Error != null) { problems.Add("refused: " + f.Error.Split('\n').Last()); return (false, 0, ms, problems); }
        if (p.Bodies.Count != 1) { problems.Add($"{p.Bodies.Count} bodies"); return (false, 0, ms, problems); }
        var b = p.Bodies.Single();
        var vr = SolidValidator.Validate(b);
        if (!vr.IsValid) problems.Add("invalid: " + string.Join(" / ", vr.Issues.Where(i => i.Severity == ValidationSeverity.Error).Take(2)));
        static double MeshVol(TSolid t) => Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(t));
        double dv = b.Guarantee.ToString() == "ExactAnalytic" ? Math.Abs(p.Volume - v0) : Math.Abs(MeshVol(b) - MeshVol(before));
        bool Tiny(TFace fz)
        {
            if (AnalyticVolume.FaceAreaRough(fz) is not { } aa) return false;
            double per = 0;
            foreach (var ce in fz.Loops.SelectMany(l => l.CoEdges)) { var pts = new List<OpenTK.Mathematics.Vector3d>(); ce.Tessellate(pts, true); for (int k = 1; k < pts.Count; k++) per += (pts[k] - pts[k - 1]).Length; }
            return Math.Abs(aa) < 1e-4 * size * size || (per > 0 && 2 * Math.Abs(aa) / per < 0.005 * size);
        }
        int tiny = b.Faces.Count(Tiny);
        if (tiny > 0) problems.Add($"{tiny} tiny face(s): " + string.Join(", ", b.Faces.Where(Tiny).Take(3).Select(x => x.Tag)));
        return (problems.Count == 0, dv, ms, problems);
    }
}
