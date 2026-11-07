using Parametric3D.Model;

static partial class Program
{
    /// <summary>
    /// k148d (замечание пользователя 01.11, 15:02, модель Sweep_Blend): ФАСКА 7.21 ПО РЕБРУ СО СМЕНОЙ ВЫПУКЛОСТИ. Ребро стенки окна
    /// лофта после скругления меняется вдоль себя из выпуклого в вогнутое; точный путь законно отказывает, резец даёт негодное
    /// тело (ребро кратности 3). Норма: лофт, скругление и первая фаска — точно; вторая фаска — отказ, и в тексте ошибки —
    /// причина (смена выпуклости), а не только «результат резца не прошёл проверку»; тело — как до фаски (точное).
    /// </summary>
    static void Krn06SweepBlendK148Checks()
    {
        Check("KRN06-USER-SWEEPBLEND-MIXED-CONVEXITY", () =>
        {
            var dir = UserModelsDir() ?? throw new Exception("tests/ui/data not found");
            var (p, pars) = DocumentFile.Load(File.ReadAllText(Path.Combine(dir, "user_k148_sweep_blend.p3d")));
            p.Rebuild(pars);
            var fs = p.Features;
            Require(fs.Count >= 4, $"features {fs.Count}");
            for (int i = 0; i < 3; i++) Require(fs[i].Error == null, $"{fs[i].Name}: {fs[i].Error}");
            // k150: с сечением стыка через вершину (P3D_DRJOINTV) резец больше не давал ребро кратности 3 — фаска строилась запасным
            // путём (многогранно). k165: с проверкой ширины фаски по граням (k160, P3D_RAILBACK) этот резец — тот же случай, что
            // фаска 9.11 и 9.72: у вершины ширина фаски на грани падает до 0.84 мм из 7.21 на участке 3.5 мм (неправильная
            // геометрия, веер треугольников). Норма — отказ с причиной, тело — как до фаски; под P3D_RAILBACK=0 — прежнее
            // (фаска резцом). Правильный угол — сопряжение фасок у вершины (BACKLOG).
            bool railBack = Environment.GetEnvironmentVariable("P3D_RAILBACK") != "0";
            if (railBack) Require(fs[3].Error != null && fs[3].Error!.Contains("does not fit on face"), $"{fs[3].Name}: want the reasoned refusal, got {fs[3].Error?.Split('\n')[0] ?? "built"}");
            else Require(fs[3].Error == null, $"{fs[3].Name}: {fs[3].Error?.Split('\n')[0]}");
            Require(p.Bodies.Count == 1, $"bodies {p.Bodies.Count}");
            var v = Parametric3D.Kernel.Topology.SolidValidator.Validate(p.Bodies[0]);
            Log(new { test = "KRN06-USER-SWEEPBLEND-MIXED-CONVEXITY", guarantee = p.Bodies[0].Guarantee.ToString(), faces = p.Bodies[0].Faces.Count(), verdict = v.Summary() });
            Require(v.IsValid, v.Summary() + " | " + string.Join(" | ", v.Issues.Where(i => i.Severity == Parametric3D.Kernel.Topology.ValidationSeverity.Error).Select(i => i.ToString()).Distinct().Take(3)));
        });
    }

    /// <summary>
    /// k148e (файл пользователя 01.11, Sweep_Blend, первый вариант): СКРУГЛЕНИЕ R5.55 У ПРЕЖНЕЙ ФАСКИ. Кромка окна упирается в
    /// ленту прежней фаски (процедурная SectionSurface), и лента скругления выходит за конец фаски: нужны неявная функция ленты
    /// за её концом (продолжение без шума — P3D_EXTSTEP, основание перпендикуляра Ньютоном с гессианом — P3D_FOOTHESS), продление
    /// прямой кромки фаски (P3D_DRSTRAIGHTEXT), лента фаски на большем промежутке (P3D_DREXTENDSECTION) и объём грани, у которой
    /// боковая сторона — из двух рёбер (P3D_BANDCHAIN). Прежде — резец (многогранно, снимал 419 мм³ вместо 147). Норма: лофт,
    /// фаска, скругление R5.55 и следующая фаска — точно, объёмы — замер k148e (до 1e-3 мм³).
    /// </summary>
    static void Krn06SweepBlendR555Checks()
    {
        Check("KRN06-USER-SWEEPBLEND-ROUND-AT-CHAMFER", () =>
        {
            var dir = UserModelsDir() ?? throw new Exception("tests/ui/data not found");
            string text = File.ReadAllText(Path.Combine(dir, "user_k148e_sweep_blend_r555.p3d"));
            // k149: и шаг 5 — R2.07 по внешней кромке (цепочка из 4 звеньев у шва лофта, гладкого только в вершинах) — точно;
            // снимается 167.37 мм³ (резец — 171.9 по сеткам; оценка по сечению на кромке 90° × 188 мм — ~170)
            var want = new[] { 645529.990445, 643912.998858, 643765.861024, 643663.308290, 643495.942641 };
            var fails = new List<string>();
            for (int k = 1; k <= 5; k++)
            {
                var (p, pars) = DocumentFile.Load(text);
                p.StageIndex = k; p.MarkStageMoved(); p.Rebuild(pars);
                var f = p.Features[k - 1];
                string g = string.Join(",", p.Bodies.Select(b => b.Guarantee));
                Log(new { step = k, feature = f.Name, error = f.Error, guarantee = g, volume = Math.Round(p.Volume, 6) });
                if (f.Error != null) fails.Add($"{k} {f.Name}: {f.Error}");
                else if (g != "ExactAnalytic") fails.Add($"{k} {f.Name}: {g}");
                else if (Math.Abs(p.Volume - want[k - 1]) > 1e-3) fails.Add($"{k} {f.Name}: volume {p.Volume:0.######}, expected {want[k - 1]}");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }

    /// <summary>
    /// k148f (последовательный корпус, s101, шаги 3–4): СКРУГЛЕНИЯ У ПРЕЖНИХ ФАСОК ЛОФТА. Шаг 3 (R0.38): точка касания ленты —
    /// на кромке прежней фаски (процедурная кривая, параметр — длина дуги), но ближайшая точка искалась по параметру на [0, 1] —
    /// «в 12 мм от ребра» (P3D_CLOSESTDOMAIN). Шаг 4 (R1.43): хребет за 1.4 мм от конца не определён, удвоение шага находило
    /// сечение за торцом в 46 мм (P3D_DRENDHOLE); грань прежней фаски, по которой прошла лента, — с нижним краем-линией касания
    /// (объём полосами по v, P3D_BANDSLAB). Норма: все шаги — точно; объём шагов 5–6 (R3, R4) — замер k148f до 1e-6 мм³, снятое
    /// скруглением R4; объём не зависит от начала отсчёта потока (центр тела и нуль) — до 1e-6 мм³.
    /// </summary>
    static void Krn06SeqS101Checks()
    {
        Check("KRN06-SEQ-S101-ROUNDS-AT-CHAMFERS", () =>
        {
            var dir = UserModelsDir() ?? throw new Exception("tests/ui/data not found");
            string text = File.ReadAllText(Path.Combine(dir, "corpus_k148f_s101_4.p3d"));
            var want = new Dictionary<int, double> { [5] = 85275.042309, [6] = 85274.563879 };
            var fails = new List<string>(); Parametric3D.Kernel.Topology.TSolid? b5 = null, b6 = null; double v5 = 0, v6 = 0;
            foreach (int k in new[] { 5, 6 })
            {
                var (p, pars) = DocumentFile.Load(text);
                p.StageIndex = k; p.MarkStageMoved(); p.Rebuild(pars);
                var f = p.Features[k - 1];
                string g = string.Join(",", p.Bodies.Select(b => b.Guarantee));
                Log(new { step = k, feature = f.Name, error = f.Error, guarantee = g, volume = Math.Round(p.Volume, 6) });
                if (f.Error != null) { fails.Add($"{k} {f.Name}: {f.Error}"); continue; }
                if (g != "ExactAnalytic") fails.Add($"{k} {f.Name}: {g}");
                if (Math.Abs(p.Volume - want[k]) > 1e-6) fails.Add($"{k} {f.Name}: volume {p.Volume:0.######}, expected {want[k]}");
                if (k == 5) { b5 = p.Bodies[0]; v5 = p.Volume; } else { b6 = p.Bodies[0]; v6 = p.Volume; }
            }
            if (b6 != null)
            {
                // независимая сверка интеграла: объём замкнутого тела не зависит от начала отсчёта потока, а ошибка области
                // отдельной грани (полосы по v) — зависит; начало — в центре тела и в нуле (центр тела в 77 мм от нуля)
                bool was = Parametric3D.Kernel.Topology.AnalyticVolume.OriginShift;
                (double? C, double? Z) Both(Parametric3D.Kernel.Topology.TSolid t)
                {
                    try
                    {
                        Parametric3D.Kernel.Topology.AnalyticVolume.OriginShift = true; var c = Parametric3D.Kernel.Topology.AnalyticVolume.OfUncached(t);
                        Parametric3D.Kernel.Topology.AnalyticVolume.OriginShift = false; var z = Parametric3D.Kernel.Topology.AnalyticVolume.OfUncached(t);
                        return (c, z);
                    }
                    finally { Parametric3D.Kernel.Topology.AnalyticVolume.OriginShift = was; }
                }
                var (vc, vz) = Both(b6); var (wc, wz) = b5 != null ? Both(b5) : (null, null);
                Log(new { originCentre = vc, originZero = vz, before = new { originCentre = wc, originZero = wz }, removed = Math.Round(v5 - v6, 6) });
                // порог — шум квадратуры (тело до правки, без грани полосами: ~1e-4 мм³ на 85 тыс.) с запасом; ошибка области
                // грани (площадь × удаление начала) — порядка 1e-2 и больше
                if (vc == null || vz == null || Math.Abs(vc.Value - vz.Value) > 1e-3) fails.Add($"volume depends on the origin: {vc} / {vz}");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }

    /// <summary>
    /// k148h: УГОЛ ЦЕПОЧКИ СКРУГЛЕНИЙ У ШВА С ВОГНУТЫМ ИЗЛОМОМ (корпус s110, шаг 0: R5.45 по кромке лофта, 4 звена) — скос по
    /// пересечению лент (митра + обрыв большей ленты гранью меньшей, как ChFi3d PerformTwoCornerbyInter / SMLib Nx2 bevel).
    /// Прежде — резец (многогранно). Мутации P3D_DRCREASE3=0, P3D_DRCREASEBEVEL=0 роняют.
    /// </summary>
    static void Krn06SeqS110Checks() =>
        SeqCornerCheck("KRN06-SEQ-S110-CREASE-BEVEL", "corpus_k148h_s110_0.p3d", 2558.9073,
            "замер k180 (начало — центр; k148h — 2558.8996: поток через край грани NURBS — равными кусками поперёк узлов, ошибка 2e-5 отн. на стенку); формула сечений по рёбрам (без углов, 12 сечений на ребро) — 2563.75 (0.2 %)");

    /// <summary>k149: корпус s106, шаг 0 — R2.71 по кромке лофта, цепочка из 7 звеньев. У одной вершины шов гладок только в ней
    /// самой и наклонён над стороной T: шар звена касается T за швом и перекатывается через ребро шва на соседнюю грань (кусок
    /// края на шве 5e-4 мм); у другого конца того же звена — вогнутый излом (скос). Прежде — резцом (многогранно).</summary>
    static void Krn06SeqS106Checks() =>
        SeqCornerCheck("KRN06-SEQ-S106-SEAM-ROLL", "corpus_k149_s106_0.p3d", S106Want,
            "замер k180 (начало — центр; k149 — 220.6858, поток поперёк узлов); формула сечений — 221.54 (0.2 %), резец снимал 225.57");
    const double S106Want = 221.0195;

    static void SeqCornerCheck(string id, string file, double want, string note)
    {
        Check(id, () =>
        {
            var dir = UserModelsDir() ?? throw new Exception("tests/ui/data not found");
            string text = File.ReadAllText(Path.Combine(dir, file));
            var (p, pars) = DocumentFile.Load(text);
            int n = p.Features.Count;
            // объём — интегралом при началах отсчёта в центре тела и в нуле: стенки лофтов корпуса — FacetedSurface, и у самого
            // лофта объём от начала зависит на ~7 мм³ из 507 тыс. (квадратура по граням-сеткам); снятый скруглением объём —
            // разность, она от начала должна почти не зависеть (ошибка области новой грани дала бы единицы мм³ и больше)
            (double C, double Z) Both(Parametric3D.Kernel.Topology.TSolid t)
            {
                bool was = Parametric3D.Kernel.Topology.AnalyticVolume.OriginShift;
                try
                {
                    Parametric3D.Kernel.Topology.AnalyticVolume.OriginShift = true; var c = Parametric3D.Kernel.Topology.AnalyticVolume.OfUncached(t);
                    Parametric3D.Kernel.Topology.AnalyticVolume.OriginShift = false; var z = Parametric3D.Kernel.Topology.AnalyticVolume.OfUncached(t);
                    return (c ?? double.NaN, z ?? double.NaN);
                }
                finally { Parametric3D.Kernel.Topology.AnalyticVolume.OriginShift = was; }
            }
            p.StageIndex = n - 1; p.MarkStageMoved(); p.Rebuild(pars);
            var before = Both(p.Bodies[0]);
            p.StageIndex = n; p.MarkStageMoved(); p.Rebuild(pars);
            var f = p.Features[n - 1];
            string g = string.Join(",", p.Bodies.Select(b => b.Guarantee));
            var fails = new List<string>();
            if (f.Error != null) fails.Add(f.Error);
            if (g != "ExactAnalytic") fails.Add("guarantee " + g);
            if (p.Bodies.Count == 1 && fails.Count == 0)
            {
                var after = Both(p.Bodies[0]);
                double rc = before.C - after.C, rz = before.Z - after.Z;
                Log(new { feature = f.Name, guarantee = g, removedCentre = Math.Round(rc, 6), removedZero = Math.Round(rz, 6), note });
                if (!(Math.Abs(rc - want) <= 1e-3)) fails.Add($"removed {rc:0.######}, expected {want:0.####}");
                // k180: поток через край по пролётам узлов — от начала снятый объём не зависит до 1e-5…5e-4 мм³ (прежде — до 7 мм³)
                if (!(Math.Abs(rc - rz) < 0.01)) fails.Add($"removed volume depends on the origin: {rc:0.######} / {rz:0.######}");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
