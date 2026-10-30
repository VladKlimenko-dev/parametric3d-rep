using OpenTK.Mathematics;
using Parametric3D.Model;

static partial class Program
{
    /// <summary>
    /// k142: СЕЧЕНИЯ ПРОТЯНУТОГО РЕЗЦА НЕ РАЗВОРАЧИВАЮТСЯ. Три законных отказа последовательного корпуса (tests/ui/data/
    /// corpus_k142_sections: s101 R0.51, s102 R3.57, s102 фаска 6 — ребро уходит под малым углом в верхнюю грань у края прежних
    /// скруглений). Отказ остаётся (складка/самопересечение у конца цепочки — открыто, BACKLOG), но сами сечения обязаны быть
    /// согласованы: у соседних станций направления к точкам A и B от ребра не расходятся больше чем на 90°, нормали граней — больше чем на 60°.
    /// Мутации: P3D_SIDEMAJORITY=0 — одиночное сечение 77 фаски 6 разворачивается целиком (сторона граней поштучно, k141);
    /// P3D_ENDNORMALINSET=0 — последнее сечение по нормали в конечной вершине (знак нормали края скругления со скачком).
    /// </summary>
    static void Krn06SweepSectionsK142Checks()
    {
        Check("KRN06-SWEEP-SECTIONS-K142", () =>
        {
            var dir = UserModelsDir() ?? throw new Exception("tests/ui/data not found");
            var fails = new List<string>();
            int n = 0;
            foreach (var file in Directory.GetFiles(Path.Combine(dir, "corpus_k142_sections"), "*.p3d").OrderBy(x => x))
            {
                string text = File.ReadAllText(file);
                var (p, pars) = DocumentFile.Load(text);
                var last = p.Features[^1];
                p.RemoveFeature(last);
                p.Rebuild(pars);
                var sets = new List<IReadOnlyList<(Vector3d P, Vector3d A, Vector3d B, Vector3d N1, Vector3d N2)>>();
                Parametric3D.Kernel.Ops.FilletOp.SweepSectionSet = s => sets.Add(s);
                try { p.AddFeature(last); p.Rebuild(pars); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { }
                finally { Parametric3D.Kernel.Ops.FilletOp.SweepSectionSet = null; }
                n++;
                if (sets.Count == 0) { fails.Add($"{Path.GetFileName(file)}: no swept attempt"); continue; }
                var s = sets[^1];
                int bad = 0; var where = new List<int>();
                for (int i = 0; i + 1 < s.Count; i++)
                {
                    double ca = Vector3d.Dot(s[i].A - s[i].P, s[i + 1].A - s[i + 1].P), cb = Vector3d.Dot(s[i].B - s[i].P, s[i + 1].B - s[i + 1].P);
                    // нормали граней у соседних станций — не дальше 60° (у гладких граней между станциями — градусы)
                    bool nj = Vector3d.Dot(s[i].N1, s[i + 1].N1) < 0.5 || Vector3d.Dot(s[i].N2, s[i + 1].N2) < 0.5;
                    if (ca < 0 || cb < 0 || nj) { bad++; if (where.Count < 4) where.Add(i); }
                    if (Environment.GetEnvironmentVariable("SECDBG") == "1" && i + 3 >= s.Count) Console.WriteLine($"SEC {i} p {s[i].P:0.###} A {s[i].A:0.###} B {s[i].B:0.###} n1 {s[i].N1:0.###} n2 {s[i].N2:0.###}");
                }
                Log(new { file = Path.GetFileName(file), stations = s.Count, flipped = bad, at = where });
                if (bad > 0) fails.Add($"{Path.GetFileName(file)}: {bad} flipped section(s) at {string.Join(",", where)} of {s.Count}");
            }
            Require(n == 3, $"expected 3 cases, found {n}");
            Require(fails.Count == 0, string.Join(" | ", fails));
        });
    }
}
