using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Model.Drawing;

static partial class Program
{
    /// <summary>
    /// k166 (замечание 05.11, 15:03, п. 1–5): изометрии с разрывами линий и сеткой на тонах.
    /// KRN-DRAW-SILGAPS-K166 — лофт корпуса (corpus_k152_s102_0) на 12 изометриях через 30°: у очерков нет разрывов (коротких
    /// невидимых кусков между видимыми). Мутация P3D_HLRSILOUT=0 — 12 разрывов (очерк по нормалям вершин закрывала сетка той же
    /// поверхности).
    /// KRN-DRAW-SHADESMOOTH-K166 — многогранная труба R92 × 559 (20 граней по окружности, источник — цилиндр, как тело из
    /// файла): у соседних (общая точка) треугольников цилиндра тон отличается не больше чем на 0.03.
    /// Мутация P3D_SHADEVN=0 — по нормали треугольника, скачки.
    /// </summary>
    static void Drawing166Checks()
    {
        Parametric3D.Kernel.Topology.TSolid Tube()
        {
            const double R = 92, L = 559; const int N = 20;
            var polys = new List<CsgPolygon>();
            Vector3d P(int i, double z) { double a = 2 * Math.PI * i / N; return new(R * Math.Cos(a), R * Math.Sin(a), z); }
            for (int i = 0; i < N; i++)
            {
                polys.Add(new CsgPolygon(new List<Vector3d> { P(i, 0), P(i + 1, 0), P(i + 1, L), P(i, L) }, 1));
            }
            polys.Add(new CsgPolygon(Enumerable.Range(0, N).Select(i => P(N - i, 0)).ToList(), 2));
            polys.Add(new CsgPolygon(Enumerable.Range(0, N).Select(i => P(i, L)).ToList(), 3));
            var src = new Dictionary<uint, Parametric3D.Kernel.Surfaces.ISurface3?>
            {
                [1] = new Parametric3D.Kernel.Surfaces.CylinderSurface(new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), R),
                [2] = new Parametric3D.Kernel.Surfaces.PlaneSurface(new Frame3(Vector3d.Zero, Vector3d.UnitY, Vector3d.UnitX)),
                [3] = new Parametric3D.Kernel.Surfaces.PlaneSurface(new Frame3(new Vector3d(0, 0, L), Vector3d.UnitX, Vector3d.UnitY)),
            };
            return BooleanOp.SolidFromPolygons(polys, src);
        }
        ViewFrame Frame(int k)
        {
            var iso = ViewFrame.Of("iso"); double a = k * Math.PI / 6;
            Vector3d Ry(Vector3d v) => new(v.X * Math.Cos(a) + v.Z * Math.Sin(a), v.Y, -v.X * Math.Sin(a) + v.Z * Math.Cos(a));
            return new ViewFrame(Ry(iso.Right), Ry(iso.Up));
        }

        Check("KRN-DRAW-SILGAPS-K166", () =>
        {
            // лофт корпуса (corpus_k152_s102_0): гладкие грани с прогибом сетки — очерки изометрий через 30° без разрывов
            var dir = UserModelsDir() ?? throw new Exception("tests/ui/data not found");
            var (p, pars) = Parametric3D.Model.DocumentFile.Load(File.ReadAllText(Path.Combine(dir, "corpus_k152_s102_0.p3d")));
            if (!p.RestoredFromFile) p.Rebuild(pars);
            int gaps = 0, sil = 0;
            for (int k = 0; k < 12; k++)
            {
                var lines = ViewProjection.ProjectMany(p.Bodies, Frame(k));
                static double Len(List<Vector2d> q) { double l = 0; for (int i = 1; i < q.Count; i++) l += (q[i] - q[i - 1]).Length; return l; }
                foreach (var grp in lines.Where(l => l.Kind == ViewLineKind.Silhouette).GroupBy(l => (l.SourceId, l.Body)))
                {
                    sil++;
                    var vis = grp.Where(l => l.Visible).ToList(); double total = grp.Sum(l => Len(l.Points));
                    foreach (var h in grp.Where(l => !l.Visible))
                    {
                        if (Len(h.Points) > 0.05 * total || Len(h.Points) > 5) continue;
                        bool Touch(Vector2d e) => vis.Any(v => (v.Points[0] - e).Length < 1e-4 || (v.Points[^1] - e).Length < 1e-4);
                        if (Touch(h.Points[0]) && Touch(h.Points[^1])) gaps++;
                    }
                }
            }
            Log(new { test = "KRN-DRAW-SILGAPS-K166", silhouettes = sil, gaps });
            Require(sil > 0, "silhouettes found");
            Require(gaps == 0, $"silhouette gaps: {gaps}");
        });

        Check("KRN-DRAW-SHADESMOOTH-K166", () =>
        {
            var b = Tube();
            var dd = new DrawingDocument { Scale = 0.5 };
            dd.Views.Add(new DrawingView { Name = "iso", X = 200, Y = 150, Style = "shaded" });
            var sc = DrawingScene.Build(dd, new[] { b });
            var sh = sc.Shades["iso"];
            // соседи — по общей точке (с точностью 1e-6)
            var at = new Dictionary<(long, long), List<int>>();
            (long, long) K(Vector2d p) => ((long)Math.Round(p.X * 1e4), (long)Math.Round(p.Y * 1e4));
            for (int i = 0; i < sh.Count; i++) foreach (var p in new[] { sh[i].A, sh[i].B, sh[i].C }) { var k = K(p); (at.TryGetValue(k, out var l) ? l : at[k] = new()).Add(i); }
            // торцы (плоскости) — свой постоянный тон; излом цилиндр — торец законный: торцы не сравниваются
            var f = ViewFrame.Of("iso"); var light = (f.Toward * 0.8 + f.Up * 0.45 + f.Right * 0.25).Normalized();
            var caps = new[] { Vector3d.UnitZ, -Vector3d.UnitZ }.Select(nn => (float)(0.35 + 0.65 * Math.Max(0, Vector3d.Dot(nn, light)))).ToArray();
            bool Cap(int i) => caps.Any(c => Math.Abs(sh[i].Shade - c) < 1e-6);
            double worst = 0;
            foreach (var l in at.Values) for (int i = 0; i < l.Count; i++) for (int j = i + 1; j < l.Count; j++) if (!Cap(l[i]) && !Cap(l[j])) worst = Math.Max(worst, Math.Abs(sh[l[i]].Shade - sh[l[j]].Shade));
            Log(new { test = "KRN-DRAW-SHADESMOOTH-K166", triangles = sh.Count, worstJump = Math.Round(worst, 4) });
            Require(sh.Count > 0, "shades built");
            Require(worst <= 0.03, $"tone jump between neighbouring triangles {worst:0.###}");
        });
    }
}
