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
            if (Environment.GetEnvironmentVariable("UPROUTE") == k.ToString()) Parametric3D.Kernel.Ops.BooleanOp.AnalyticRouteDebug = m => Console.Error.WriteLine("   route: " + m); else Parametric3D.Kernel.Ops.BooleanOp.AnalyticRouteDebug = null;
            Parametric3D.Kernel.Ops.DirectRound.Debug = Environment.GetEnvironmentVariable("UPDR") == k.ToString() ? m => Console.Error.WriteLine("   dr: " + m) : null;
            Parametric3D.Kernel.Geometry.Ssi.FootDebug = Environment.GetEnvironmentVariable("UPDR") == k.ToString() && Environment.GetEnvironmentVariable("UPFOOT") == "1" ? m => Console.Error.WriteLine("   foot: " + m) : null;
            bool prof = Environment.GetEnvironmentVariable("UPPROF") == k.ToString();
            if (prof) { Parametric3D.Kernel.Diagnostics.Prof.Enabled = true; Parametric3D.Kernel.Diagnostics.Prof.Reset(); }
            if (Environment.GetEnvironmentVariable("UPDEFER") == "1")
            {   // как приложение: обычный расчёт с отложенными проверками
                int th = int.TryParse(Environment.GetEnvironmentVariable("UPTHREADS"), out var tv) && tv > 0 ? tv : 1;   // потоки, как в приложении
                var ks = Parametric3D.Kernel.KernelSettings.Capture() with { Preview = true, LivePreview = Environment.GetEnvironmentVariable("UPLIVE") == "1" };
                if (th > 1) ks = ks with { BooleanThreads = th, MeshThreads = th };
                using (new Parametric3D.Kernel.KernelContext(ks).Enter()) p.Rebuild(pars);
            }
            else p.Rebuild(pars);
            if (prof) { Parametric3D.Kernel.Diagnostics.Prof.Enabled = false; Console.Error.WriteLine(Parametric3D.Kernel.Diagnostics.Prof.Report()); }
            Parametric3D.Kernel.Analytic.GeneralBoolean.PointDebug = null; Parametric3D.Kernel.Analytic.FaceIntersect.Debug = null;
            var f = p.Features[k - 1];
            if (Environment.GetEnvironmentVariable("UPMESH") == "1")
                foreach (var b in p.Bodies)
                {
                    var m = Parametric3D.Kernel.Tess.Tessellator.Tessellate(b);
                    var sides = new Dictionary<(long, long, long, long, long, long), int>(); var sideFace = new Dictionary<(long, long, long, long, long, long), uint>();
                    (long, long, long) K(OpenTK.Mathematics.Vector3d q) => ((long)Math.Round(q.X * 1e3), (long)Math.Round(q.Y * 1e3), (long)Math.Round(q.Z * 1e3));
                    var ix = m.Indices; OpenTK.Mathematics.Vector3d Pt(int i) => new OpenTK.Mathematics.Vector3d(m.Vertices[i].P.X, m.Vertices[i].P.Y, m.Vertices[i].P.Z);
                    for (int t = 0; t + 2 < ix.Length; t += 3)
                        for (int e = 0; e < 3; e++)
                        {
                            var a = K(Pt(ix[t + e])); var c = K(Pt(ix[t + (e + 1) % 3]));
                            var key = a.CompareTo(c) < 0 ? (a.Item1, a.Item2, a.Item3, c.Item1, c.Item2, c.Item3) : (c.Item1, c.Item2, c.Item3, a.Item1, a.Item2, a.Item3);
                            sides[key] = sides.GetValueOrDefault(key) + 1;
                            if (!sideFace.ContainsKey(key)) sideFace[key] = m.Vertices[ix[t]].FaceId;
                        }
                    foreach (var g in sides.Where(kv => kv.Value == 1).GroupBy(kv => sideFace[kv.Key]))
                    {
                        var fc = b.Faces.FirstOrDefault(x => x.Id == g.Key);
                        Console.Error.WriteLine($"   open on face {g.Key} {fc?.Tag} {fc?.Surface.GetType().Name}{(fc?.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface fsd ? $" facets {fsd.Facets.Count} of {fsd.Source?.GetType().Name} fromLoops {fsd.FromLoops}" : "")}: {g.Count()} e.g. ({g.First().Key.Item1 / 1e3},{g.First().Key.Item2 / 1e3},{g.First().Key.Item3 / 1e3}); body meshKey {b.DisplayMeshKey} rev {b.MeshRevision}/{b.Revision} exact {b.Exact != null} auth {b.ExactAuthoritative}");
                    }
                    Console.Error.WriteLine($"MESH step {k}: tris {ix.Length / 3}, open sides {sides.Values.Count(v => v == 1)}, over-shared {sides.Values.Count(v => v > 2)}, volume {Parametric3D.Kernel.Tess.MassProps.Volume(m):0.###}, previewMesh {Parametric3D.Kernel.Ops.BooleanOp.IsPreviewMesh(b)}");
                }
            Log(new { step = k, feature = f.Name, kind = f.GetType().Name, error = f.Error, bodies = p.Bodies.Count, guarantee = string.Join(",", p.Bodies.Select(b => b.Guarantee)),
                      faces = p.Bodies.Sum(b => b.Faces.Count()), volume = double.IsFinite(p.Volume) ? (object)p.Volume : p.Volume.ToString(), volErr = double.IsFinite(p.VolumeError) ? (object)p.VolumeError : p.VolumeError.ToString(), downgrades = string.Join(";", p.Downgrades.Select(d => $"{d.Key.Name}:{d.Value.Faces}f/{d.Value.Edges}e")),
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
            Log(new { test = "KRN09-FILE-DISPLAY-RAWSIG", cached = q.ShowsCached, restored = q.RestoredFromFile });
            // k153: с телом в файле деталь восстанавливается целиком (RestoredFromFile) — это тоже принятый показ
            Require(q.ShowsCached || q.RestoredFromFile, "the display stored in the file was rejected");
        });
    }
}
