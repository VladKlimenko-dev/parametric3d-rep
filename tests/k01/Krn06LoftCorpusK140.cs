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
    /// <summary>
    /// Я1 (REFERENCE_SOLUTIONS п. 6; BACKLOG k149): ШАГ В СЕАНСЕ = ТОТ ЖЕ ШАГ ИЗ ФАЙЛА, ПОБИТОВО. Модель корпуса s106, 6 правок
    /// подряд (как loftseq, с отладочным обработчиком прямого построителя); после каждой — перестроение из текста документа
    /// без тел и сравнение тел побитово (вершины) и по отказу, телам, гарантии, объёму. Прежде расходилось с первой правки:
    /// основание перпендикуляра на NURBS зависело от подсказки прежних запросов (мутация P3D_FOOTCANON=0), производная
    /// многогранная форма — от треугольников, что лежат на гранях (мутация P3D_LAZYREMESH=0; шаг 5: откат на 478 мм³ в сеансе
    /// против годного тела из файла).
    /// </summary>
    static void Krn01SessionEqualsFileChecks()
    {
        Check("KRN-Y1-SESSION-EQUALS-FILE", () =>
        {
            var was = Environment.GetEnvironmentVariable("SEQ_REBUILDCHECK");
            Environment.SetEnvironmentVariable("SEQ_REBUILDCHECK", "1");
            try { LoftCorpusSeq(1, 106, 6, Path.Combine(Path.GetTempPath(), "p3d_y1_seq")); }
            finally { Environment.SetEnvironmentVariable("SEQ_REBUILDCHECK", was); }
            var (n, d, b) = LastSeqRebuild;
            Log(new { test = "KRN-Y1-SESSION-EQUALS-FILE", steps = n, diff = d, bitsDiff = b });
            Require(n >= 5, $"only {n} steps checked");
            Require(d == 0 && b == 0, $"session differs from file: {d} step(s) by result, {b} step(s) bitwise");
        });
    }

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
                // k147: третий столбец «noswitch» — история записана до перехода боковой точки фаски (прежние операции шли
                // резцом); воспроизводится так же, иначе тело перед последней операцией другое и случай не о том
                bool noSwitch = row.Length > 2 && row[2].Trim().StartsWith("noswitch");
                Parametric3D.Kernel.Ops.DirectRound.ChamferSwitchOffForTest = noSwitch;
                // k148h: «nocrease» — история до точного угла у излома шва (прежние операции резцом), воспроизводится так же
                Parametric3D.Kernel.Ops.DirectRound.CreaseOffForTest = row.Length > 2 && row[2].Trim() == "nocrease";
                // Я2: «norefine» — история до мелкой таблицы ленты (прежние операции резцом), воспроизводится так же
                Parametric3D.Kernel.Ops.DirectRound.TabRefineOffForTest = row.Length > 2 && row[2].Trim() == "norefine";
                (bool Ok, double Dv, long Ms, List<string> Problems) r;
                try { r = CorpusFixture(Path.Combine(cdir, file)); } finally { Parametric3D.Kernel.Ops.DirectRound.ChamferSwitchOffForTest = false; Parametric3D.Kernel.Ops.DirectRound.CreaseOffForTest = false; Parametric3D.Kernel.Ops.DirectRound.TabRefineOffForTest = false; }
                // k159: у ожидаемого отказа («overflow») — отметка expected, чтобы итог регрессии не считал строку провалом
                Log(new { file, r.Ok, dv = Math.Round(r.Dv, 4), want, r.Ms, r.Problems, expected = row.Length > 2 && row[2].Contains("overflow") ? "refusal" : null });
                // k153: «overflow» — фаска шире грани ребра (съедает полосу шириной 0.05 мм у s101_2, прежнюю фаску 1.7 мм у
                // s102_5): норма — отказ с причиной «wider than the face» (под P3D_CHAMFEROVERFLOW=0 — прежнее построение)
                if (row.Length > 2 && row[2].Contains("overflow"))
                {
                    bool refusedOk = !r.Ok && r.Problems.Count == 1 && r.Problems[0].Contains("wider than the face");
                    if (!refusedOk && !OverflowCheckOff) fails.Add($"{file}: expected the 'wider than the face' refusal, got " + (r.Ok ? $"a body (dv {r.Dv:0.####})" : string.Join("; ", r.Problems)));
                    if (!OverflowCheckOff) continue;
                }
                foreach (var pr in r.Problems) fails.Add($"{file}: {pr}");
                if (r.Ok && Math.Abs(r.Dv - want) > 0.01 * Math.Abs(want) + 0.05) fails.Add($"{file}: removed volume {r.Dv:0.####}, expected {want:0.####}");
            }
            Require(n > 0, "no corpus cases");
            Require(fails.Count == 0, string.Join(" | ", fails.Take(8)));
        });
    }

    static readonly bool OverflowCheckOff = Environment.GetEnvironmentVariable("P3D_CHAMFEROVERFLOW") == "0";

    /// <summary>Случай корпуса: последняя операция файла — над телом без неё; замеры как в LoftCorpusRun.</summary>
    static (bool Ok, double Dv, long Ms, List<string> Problems) CorpusFixture(string file)
    {
        var problems = new List<string>();
        if (Environment.GetEnvironmentVariable("LOFT_DRALL") == "1") Parametric3D.Kernel.Ops.DirectRound.Debug = s => Console.Error.WriteLine("DR " + (s.Length > 3000 ? s[..3000] : s));
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
