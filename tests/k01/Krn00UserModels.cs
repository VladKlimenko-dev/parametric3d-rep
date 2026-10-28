using System.Globalization;
using Parametric3D.Kernel.Topology;

/// <summary>
/// KRN-00 (00.3): КОРПУС МОДЕЛЕЙ ПОЛЬЗОВАТЕЛЯ С ЭТАЛОНАМИ. Детали, присланные пользователем (tests/ui/data/user_*.p3d),
/// перестраиваются ядром с нуля; ответ сверяется с эталоном (tests/ui/data/user_models.tsv): ошибки операций, число тел
/// и граней, объём и площадь (относительно 1e-9), годность тел. Эталон пишется режимом «usermodels-ref» и меняется
/// только осознанно (изменение ответа ядра на модели пользователя — разбирается). Тот же корпус и эталон — в кнопке
/// «Тест» приложения (раздел «Модели пользователя»): совпадение на Windows — воспроизведение результатов другим
/// человеком на другой платформе (00.5).
/// </summary>
static partial class Program
{
    public sealed record UserModelAnswer(string File, int Features, int Errors, int Bodies, int Faces, double Volume, double Area, bool Valid, double Ms, string Sketches, double PointSum, string Guarantee);

    static string? UserModelsDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d != null; i++, d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "tests", "ui", "data");
            if (Directory.Exists(p) && Directory.GetFiles(p, "user_*.p3d").Length > 0) return p;
        }
        var env = Environment.GetEnvironmentVariable("P3D_USERMODELS");
        return env != null && Directory.Exists(env) ? env : null;
    }

    public static UserModelAnswer AnswerOf(string path)
    {
        var (part, pars) = Parametric3D.Model.DocumentFile.Load(File.ReadAllText(path));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        part.Rebuild(pars);
        double ms = sw.Elapsed.TotalMilliseconds;
        bool valid = part.Bodies.All(b => SolidValidator.Validate(b).IsValid);
        string guar = string.Join(",", part.Bodies.Select(b => b.Guarantee.ToString()));
        // эскизы: элементы/размеры/состояние решения/свободные степени; контрольная сумма точек после решения
        var sks = new List<string>(); double psum = 0;
        foreach (var sk in part.Sketches)
        {
            var st = sk.Solve(new Parametric3D.Model.Sketching.NumericSketchSolver());
            sks.Add($"{sk.Entities.Count}/{sk.Dimensions.Count}/{st}/{sk.FreeDegrees}");
            foreach (var q in sk.AllPoints()) psum += Math.Abs(q.X) + Math.Abs(q.Y);
        }
        return new UserModelAnswer(Path.GetFileName(path), part.Features.Count, part.Features.Count(f => f.Error != null),
            part.Bodies.Count, part.Bodies.Sum(b => b.Faces.Count()), part.Volume, part.Area, valid, ms, string.Join(";", sks), psum, guar);
    }

    static string Fmt(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    static void UserModelsRef()
    {
        var dir = UserModelsDir() ?? throw new Exception("tests/ui/data not found");
        var lines = new List<string> { "# KRN-00 (00.3): эталон моделей пользователя — файл, операций, ошибок, тел, граней, объём мм³, площадь мм², тела годны, эскизы (элементы/размеры/решение/свободные степени), сумма |x|+|y| точек эскизов, уровень гарантии тел" };
        foreach (var f in Directory.GetFiles(dir, "user_*.p3d").OrderBy(x => x, StringComparer.Ordinal))
        {
            var a = AnswerOf(f);
            lines.Add(string.Join('\t', a.File, a.Features, a.Errors, a.Bodies, a.Faces, Fmt(a.Volume), Fmt(a.Area), a.Valid ? 1 : 0, a.Sketches, Fmt(a.PointSum), a.Guarantee));
            Log(new { model = a.File, a.Features, a.Errors, a.Bodies, a.Faces, a.Volume, a.Area, a.Valid, ms = Math.Round(a.Ms) });
        }
        File.WriteAllLines(Path.Combine(dir, "user_models.tsv"), lines);
    }

    static void Krn00UserModelChecks()
    {
        Check("KRN05-USER-X05-FULL-TREE", () =>
        {
            // k133: деталь пользователя X-218795-05 целиком (полоса отката снята): вырез — точное тело (P3D_PCARCDEV=0,
            // P3D_CLIPONEDGE=0, P3D_CLIPBRIDGE=0 — многогранное), круговой массив выреза строится (P3D_TANGENTFAR=0 — отказ)
            var dir = UserModelsDir() ?? throw new Exception("tests/ui/data not found");
            var (p, pars) = Parametric3D.Model.DocumentFile.Load(File.ReadAllText(Path.Combine(dir, "user_k132_x05.p3d")));
            p.StageIndex = -1; p.MarkStageMoved(); p.Rebuild(pars);
            var cut = p.Features.First(f => f is Parametric3D.Model.Features.ExtrudeFeature);
            Log(new { test = "KRN05-USER-X05-FULL-TREE", errors = string.Join("; ", p.Features.Where(f => f.Error != null).Select(f => f.Name + ": " + f.Error)), downgrades = string.Join(";", p.Downgrades.Select(d => d.Key.Name)), faces = p.Bodies.Sum(b => b.Faces.Count()), volume = p.Volume });
            Require(p.Features.All(f => f.Error == null), "feature errors: " + string.Join("; ", p.Features.Where(f => f.Error != null).Select(f => f.Name + ": " + f.Error)));
            Require(!p.Downgrades.ContainsKey(cut), "the cut went polyhedral");
        });

        Check("KRN00-USER-MODELS", () =>
        {
            var dir = UserModelsDir() ?? throw new Exception("tests/ui/data not found");
            var refs = File.ReadAllLines(Path.Combine(dir, "user_models.tsv")).Where(l => !l.StartsWith('#') && l.Length > 0).Select(l => l.Split('\t')).ToList();
            var files = Directory.GetFiles(dir, "user_*.p3d").Select(Path.GetFileName).ToHashSet();
            var fails = new List<string>();
            foreach (var f in files) if (!refs.Any(r => r[0] == f)) fails.Add($"{f}: no reference");
            bool mut = Environment.GetEnvironmentVariable("P3D_USERMODELS_MUT") == "1";
            foreach (var r in refs)
            {
                var a = AnswerOf(Path.Combine(dir, r[0]));
                double v = double.Parse(r[5], CultureInfo.InvariantCulture), ar = double.Parse(r[6], CultureInfo.InvariantCulture);
                if (mut) v *= 1 + 1e-6;   // мутация проверки: эталон объёма сдвинут на 1e-6
                bool Rel(double x, double y) => Math.Abs(x - y) <= 1e-9 * Math.Max(1, Math.Abs(y));
                var diff = new List<string>();
                if (a.Features != int.Parse(r[1])) diff.Add($"features {a.Features}≠{r[1]}");
                if (a.Errors != int.Parse(r[2])) diff.Add($"errors {a.Errors}≠{r[2]}");
                if (a.Bodies != int.Parse(r[3])) diff.Add($"bodies {a.Bodies}≠{r[3]}");
                if (a.Faces != int.Parse(r[4])) diff.Add($"faces {a.Faces}≠{r[4]}");
                if (!Rel(a.Volume, v)) diff.Add($"volume {Fmt(a.Volume)}≠{Fmt(v)}");
                if (!Rel(a.Area, ar)) diff.Add($"area {Fmt(a.Area)}≠{Fmt(ar)}");
                if ((a.Valid ? "1" : "0") != r[7]) diff.Add($"valid {a.Valid}");
                if (a.Sketches != r[8]) diff.Add($"sketches {a.Sketches}≠{r[8]}");
                double ps = double.Parse(r[9], CultureInfo.InvariantCulture);
                if (Math.Abs(a.PointSum - ps) > 1e-6 * Math.Max(1, ps)) diff.Add($"sketch points {Fmt(a.PointSum)}≠{Fmt(ps)}");
                if (r.Length > 10 && a.Guarantee != r[10]) diff.Add($"guarantee {a.Guarantee}≠{r[10]}");
                Log(new { model = a.File, a.Features, a.Errors, a.Faces, a.Volume, ms = Math.Round(a.Ms), same = diff.Count == 0 });
                if (diff.Count > 0) fails.Add($"{a.File}: {string.Join(", ", diff)}");
            }
            Require(refs.Count >= 5 && fails.Count == 0, string.Join("; ", fails));
        });
    }
}
