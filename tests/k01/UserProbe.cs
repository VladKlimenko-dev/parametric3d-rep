static partial class Program
{
    /// <summary>Разбор модели пользователя по шагам дерева: уровень гарантии тела, грани, объём, понижения геометрии.</summary>
    static void UserProbe(string path)
    {
        for (int k = 1; ; k++)
        {
            var (p, pars) = Parametric3D.Model.DocumentFile.Load(File.ReadAllText(path));
            if (k > p.Features.Count) break;
            p.StageIndex = k; p.MarkStageMoved();
            if (Environment.GetEnvironmentVariable("UPDBG") == k.ToString()) { Parametric3D.Kernel.Analytic.GeneralBoolean.PointDebug = (m, q) => Console.Error.WriteLine($"PD {m} {q}"); Parametric3D.Kernel.Analytic.FaceIntersect.Debug = m => Console.Error.WriteLine("FI " + m); }
            p.Rebuild(pars);
            Parametric3D.Kernel.Analytic.GeneralBoolean.PointDebug = null; Parametric3D.Kernel.Analytic.FaceIntersect.Debug = null;
            var f = p.Features[k - 1];
            Log(new { step = k, feature = f.Name, kind = f.GetType().Name, error = f.Error, bodies = p.Bodies.Count, guarantee = string.Join(",", p.Bodies.Select(b => b.Guarantee)),
                      faces = p.Bodies.Sum(b => b.Faces.Count()), volume = p.Volume, volErr = p.VolumeError, downgrades = string.Join(";", p.Downgrades.Select(d => $"{d.Key.Name}:{d.Value.Faces}f/{d.Value.Edges}e")),
                      integrity = p.Integrity });
        }
    }
}

static partial class Program
{
    /// <summary>Разбор: принята ли сетка показа из файла (подпись описания).</summary>
    static void DisplaySigProbe(string path)
    {
        long r0 = Parametric3D.Model.DocumentFile.DisplayRejected;
        var (p, _) = Parametric3D.Model.DocumentFile.Load(File.ReadAllText(path));
        Log(new { file = Path.GetFileName(path), cached = p.ShowsCached, rejected = Parametric3D.Model.DocumentFile.DisplayRejected - r0 });
    }
}

static partial class Program
{
    static void Krn09DisplayRawSigChecks()
    {
        Check("KRN09-FILE-DISPLAY-RAWSIG", () =>
        {
            // k133 (замечание 29.10, 08:55, п. 5): сетка показа из файла принимается, если описание модели в файле то же, что при
            // записи, — даже когда повторная запись разобранного описания другая (поле, которого эта сборка не знает, или поле,
            // добавленное после записи файла). Прежде подпись бралась по повторной записи — сетка отбрасывалась, деталь
            // появлялась только после полного перестроения (P3D_DISPLAYRAWSIG=0).
            var p = new Parametric3D.Model.Part("rawsig"); Parametric3D.Model.TestGeometry.AddCube(p, 40); p.Rebuild(null);
            string text = Parametric3D.Model.DocumentFile.Save(p, null, withDisplay: true);
            Require(text.Contains("\"Display\""), "no display in the file");
            // поле, которого описание не знает (как у файла другой сборки), — внутри описания, до сетки
            string other = text.Replace("\"Name\": \"rawsig\"", "\"Name\": \"rawsig\",\n  \"FutureField\": 7");
            Require(other != text, "the test field was not inserted");
            // подпись записана по тексту с этим полем (как у сборки, которая его знает)
            var raw = Parametric3D.Model.DocumentFile.StripDisplay(other)!;
            other = Parametric3D.Model.DocumentFile.ResignForTest(other, raw);
            var (q, _) = Parametric3D.Model.DocumentFile.Load(other);
            Log(new { test = "KRN09-FILE-DISPLAY-RAWSIG", cached = q.ShowsCached });
            Require(q.ShowsCached, "the display stored in the file was rejected");
        });
    }
}
