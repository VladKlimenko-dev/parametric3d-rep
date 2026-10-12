using Parametric3D.Kernel;
using OpenTK.Mathematics;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

/// <summary>
/// g.11: СЕТКА ПОКАЗА ТЕЛА С АВТОРИТЕТНОЙ МНОГОГРАННОЙ ФОРМОЙ — МНОГООБРАЗНА ВО FLOAT. Случаи разностных серий 8 и 9, где
/// показ складывался при годной точной форме: щепки шириной 1e-6 мм (конус почти касается цилиндра или шара), вершины ближе
/// допуска сварки, видимая сетка граней вместо фасет, разделение формы по видимой сетке, p-кривая дуги с периодом ≠ 1.
/// Повтор — по зерну и номеру (прочие случаи серии не вычисляются; число операций предшествующих — строкой ниже).
/// </summary>
static partial class Program
{
    // число операций случаев 0…N серии (из манифестов diff8g, diff9e)
    const string G11Ops8 = "22212222222212122211122111212111111111111122121112112222111121111122211122211111121";
    const string G11Ops9 = "12211221211221222212121221111211122112122212212221222221121222211212211111221211111111122122111211112122112112222122121121212112221211221";

    static void G11PolyChecks()
    {
        Check("G11-POLY-DISPLAY-MANIFOLD-SLIVERS", () =>
        {
            var cases = new (int Seed, int Index, string Why)[]
            {
                (8, 0, "щепка у тора: сторона кратности 3"),
                (8, 82, "вершины ближе допуска сварки"),
                (9, 20, "p-кривая дуги на выдавливании (период 2π/размах)"),
                (9, 46, "изогнутая щепка вдоль кривой пересечения (129 игл) с дыркой"),
                (9, 65, "две операции: обход по нормалям переворачивал участок"),
                (9, 66, "касание тела в вершине после сведения"),
                (9, 91, "разделение формы по видимой сетке (0.25%)"),
                (9, 136, "щепка шириной 1e-6 мм: конус у цилиндра"),
                (9, 50, "фасеты плоской грани не согласованы с соседями — выбор видимой сетки"),
                (9, 129, "чистка ухудшала годную сетку — сравнение с обычной сваркой"),
            };
            var fails = new List<string>();
            foreach (var (seed, idx, why) in cases)
            {
                string ops = seed == 8 ? G11Ops8 : G11Ops9;
                var dir = Path.Combine(Path.GetTempPath(), $"g11_{seed}_{idx}_{Environment.ProcessId}");
                Directory.CreateDirectory(dir);
                var rp = Path.Combine(dir, "replay.tsv");
                var lines = new List<string> { "id\tops" };
                for (int i = 0; i < idx; i++) lines.Add($"d{seed}_{i:D4}\t{string.Join(",", Enumerable.Repeat("x", ops[i] - '0'))}");
                File.WriteAllLines(rp, lines);
                string id = $"d{seed}_{idx:D4}";
                Environment.SetEnvironmentVariable("DIFFREPLAY", rp);
                Environment.SetEnvironmentVariable("DIFFONLY", id);
                try { DiffDump(dir, seed, idx + 1); }
                finally { Environment.SetEnvironmentVariable("DIFFREPLAY", null); Environment.SetEnvironmentVariable("DIFFONLY", null); }
                var row = File.ReadAllLines(Path.Combine(dir, "manifest.tsv")).Last().Split('\t');
                string outcome = row.Length > 3 ? row[3] : "?", valid = row.Length > 13 ? row[13] : "?";
                if (outcome != "Success" || valid != "ok") fails.Add($"{id} ({why}): {outcome} {valid}");
                try { Directory.Delete(dir, true); } catch { }
            }
            if (fails.Count > 0) throw new Exception(string.Join("; ", fails));
        });
        // Найдено после закрытия g.11 (проба тора): прямоугольник минус тор — многогранный результат с негодной сеткой показа
        // (164/159 сторон без пары): допуск разделения тела по форме, фасеты области разделённой грани, метка источника у
        // компонент кривой грани. Объём — Монте-Карло по формулам, 5σ.
        Check("G11-POLY-BOX-MINUS-TORUS", () =>
        {
            var c = new Vector3d(1, 2, 3); double R = 10, r = 3;
            var box = Box(c.X - 5, c.Y - 5, c.Z - 2, 20, 10, 4);
            var res = BooleanOp.Evaluate(new List<TSolid> { box }, PrimitiveOp.Torus(c, R, r), BooleanOp.BooleanKind.Subtract, Parametric3D.Kernel.OperationPolicy.Strict, new List<string>());
            if (res.Outcome != OperationOutcome.Success) throw new Exception("исход " + res.Outcome);
            var fails = new List<string>();
            foreach (var bd in res.Bodies) { var v = SolidValidator.Validate(bd); if (v.Issues.Any(i => i.Severity == ValidationSeverity.Error)) fails.Add(v.Summary()); }
            var rnd = new Random(7); int nIn = 0, N = 400000;
            for (int i = 0; i < N; i++)
            {
                var p = new Vector3d(c.X - 5 + 20 * rnd.NextDouble(), c.Y - 5 + 10 * rnd.NextDouble(), c.Z - 2 + 4 * rnd.NextDouble());
                var q = p - c; double rho = Math.Sqrt(q.X * q.X + q.Y * q.Y);
                if ((rho - R) * (rho - R) + q.Z * q.Z >= r * r) nIn++;
            }
            double vb = 800, mc = vb * nIn / N, sg = vb * Math.Sqrt((double)nIn / N * (1 - (double)nIn / N) / N);
            double vol = res.Bodies.Sum(x => AnalyticVolume.Of(x) ?? double.NaN);
            if (!(Math.Abs(vol - mc) < 5 * sg + 1)) fails.Add($"объём {vol:0.###}, МК {mc:0.#}±{sg:0.#}");
            if (fails.Count > 0) throw new Exception(string.Join("; ", fails));
        });
    }
}
