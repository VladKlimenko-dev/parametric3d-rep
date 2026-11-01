using Parametric3D.Model;

static partial class Program
{
    /// <summary>
    /// k136 (модель пользователя после правки Sketch1, 29.10): КРУГОВОЙ МАССИВ ВЫРЕЗА НА ТЕЛЕ ВРАЩЕНИЯ — ТОЧНО.
    /// Массив берёт операцию заново и строит 6 копий; копия 5 падала («ребро 5.46e-8 мм»), потому что:
    /// — сам вырез уходил в многогранный путь: удостоверение событий требовало кривую пересечения через точку ребра тонкого
    ///   тора на ПОВЕРХНОСТИ цилиндра выреза вне его грани (P3D_CERTOUTBOX=0);
    /// — у копии замыкающее звено ломаной тор × цилиндр было длиннее шара единственности ветви (P3D_TRACECHORD=0);
    /// — у копии через шов контур грани вращения подходил к себе на 1.3e-6 мм, и клин между этими точками отвергал разметку
    ///   грани (P3D_PINCHTRI=0).
    /// Норма: все операции без ошибок, тело точное аналитическое, объём массива = объём вращения − 6 × объём одного выреза
    /// (до 1e-9).
    /// </summary>
    static void Krn05X05PatternChecks()
    {
        Check("KRN05-USER-X05-PATTERN-EXACT", () =>
        {
            var dir = UserModelsDir() ?? throw new Exception("tests/ui/data not found");
            var text = File.ReadAllText(Path.Combine(dir, "user_k136_x05.p3d"));
            double V(int stage)
            {
                var (p, pars) = DocumentFile.Load(text);
                p.StageIndex = stage; p.MarkStageMoved(); p.Rebuild(pars);
                var bad = p.Features.Take(stage).Where(f => f.Error != null).Select(f => f.Name + ": " + f.Error!.Split('\n')[0]).ToList();
                Require(bad.Count == 0, string.Join("; ", bad));
                Require(p.Bodies.Count == 1 && p.Bodies[0].Guarantee == Parametric3D.Kernel.GuaranteeLevel.ExactAnalytic,
                        $"stage {stage}: guarantee {string.Join(",", p.Bodies.Select(b => b.Guarantee))}");
                return p.Volume;
            }
            double v1 = V(1), v2 = V(2), v3 = V(3);
            double want = v1 - 6 * (v1 - v2);
            Log(new { revolve = v1, cut = v2, pattern = v3, want, rel = Math.Abs(v3 - want) / want });
            Require(Math.Abs(v3 - want) <= 1e-9 * want, $"pattern volume {v3} vs {want}");
        });
        // k148 (замечание 01.11, 14:26: у пользователя на Windows копия 4 массива — «4 boundary side(s) without a partner»; на
        // Linux не воспроизводилось): ОСЬ МАССИВА СО СДВИГОМ 1e-10 мм И НАКЛОНОМ 1e-12 рад — шум последних битов поворота копий.
        // Дно выреза касается цилиндров тела вращения (R 209.956, R 148.336), стенки копий параллельны оси: пересечение
        // плоскости с цилиндром — по допускам точности модели (почти параллельно — прямые, а не эллипс в 1e14 мм; почти
        // касание — касание, а не «пусто»). Норма: все сдвиги — без ошибки, тело одно (точное или честно многогранное), объём
        // массива — как без сдвига (1e-4; многогранный путь — 7.6e-5). Точно при сдвиге — пока нет (BACKLOG).
        // Мутации P3D_PCNEARPAR=0, P3D_PCTANACC=0 роняют проверку (копия массива — отказ).
        Check("KRN05-USER-X05-PATTERN-JITTER", () =>
        {
            var dir = UserModelsDir() ?? throw new Exception("tests/ui/data not found");
            var text = File.ReadAllText(Path.Combine(dir, "user_k136_x05.p3d"));
            var fails = new List<string>();
            double? v0 = null;
            // точность обязательна у сдвигов, где её дают правки k148 (без них — многогранно)
            var jit = new (double X, double Y, double DX, double DY, bool Exact)[]
            {
                (0, 0, 0, 0, true),
                (-2.6752654604584286e-11, 2.3353611052648708e-11, -2.8390276373399945e-13, 9.230921258558312e-14, true),   // r04
                (-1.9674908190853302e-10, -1.5733532307339545e-11, -1.2890985371068457e-12, 2.2259673343920537e-13, true), // r02
                (4.368412489253348e-11, -5.62751282220761e-11, 5.414150271953954e-13, -4.965035286751232e-13, false),     // r16: у пользователя — отказ копии 4
                (-3.221039583527476e-10, -2.3509983055906493e-11, 3.032028423309729e-12, 1.0188073443866145e-12, false),  // r01
            };
            foreach (var (x, y, dx, dy, exact) in jit)
            {
                var (p, pars) = DocumentFile.Load(text);
                var pat = p.Features.OfType<Parametric3D.Model.Features.CircularPatternFeature>().Single();
                pat.At += new OpenTK.Mathematics.Vector3d(x, y, 0);
                pat.Axis = (pat.Axis + new OpenTK.Mathematics.Vector3d(dx, dy, 0)).Normalized();
                p.Rebuild(pars);
                var bad = p.Features.Where(f => f.Error != null).Select(f => f.Name + ": " + f.Error!.Split('\n')[0]).ToList();
                string tag = $"jitter ({x:E1},{y:E1}; {dx:E1},{dy:E1})";
                Log(new { test = "KRN05-USER-X05-PATTERN-JITTER", tag, volume = p.Volume, guarantee = string.Join(",", p.Bodies.Select(b => b.Guarantee)), faces = p.Bodies.Sum(b => b.Faces.Count()), error = bad.FirstOrDefault() });
                if (bad.Count > 0) { fails.Add($"{tag}: {bad[0]}"); continue; }
                if (exact && p.Bodies.Count == 1 && p.Bodies[0].Guarantee != Parametric3D.Kernel.GuaranteeLevel.ExactAnalytic) fails.Add($"{tag}: not exact ({p.Bodies[0].Guarantee})");
                if (p.Bodies.Count != 1 || p.Bodies[0].Guarantee is not (Parametric3D.Kernel.GuaranteeLevel.ExactAnalytic or Parametric3D.Kernel.GuaranteeLevel.ExactPolyhedral)) fails.Add($"{tag}: guarantee {string.Join(",", p.Bodies.Select(b => b.Guarantee))}");
                v0 ??= p.Volume;
                if (Math.Abs(p.Volume - v0.Value) > 1e-4 * v0.Value) fails.Add($"{tag}: volume {p.Volume} vs {v0}");
            }
            Require(fails.Count == 0, string.Join(" | ", fails));
        });
    }
}
