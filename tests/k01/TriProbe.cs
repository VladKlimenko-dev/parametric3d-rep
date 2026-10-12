using OpenTK.Mathematics;
using Parametric3D.Kernel.Tess;

/// <summary>
/// KRN-02 (02.4): триангуляция плоских контуров на вырожденных раскладках — отверстия на общих прямых, вершины на одной
/// прямой, гребёнки, — в разных масштабах, поворотах и удалениях от начала. Годность: сумма площадей треугольников со
/// знаком равна площади области, сумма модулей — тоже (нет вывернутых и наложенных), каждый треугольник не вывернут.
/// </summary>
static class TriProbe
{
    public record Res(int Cases, int Bad, string FirstBad);

    static List<Vector2d> Rect(double x0, double y0, double x1, double y1, int subdiv)
    {
        // Прямоугольник против часовой; на сторонах — лишние вершины на той же прямой.
        var r = new List<Vector2d>();
        void Side(Vector2d a, Vector2d b) { for (int i = 0; i < subdiv; i++) r.Add(a + (b - a) * (i / (double)subdiv)); }
        Side(new(x0, y0), new(x1, y0)); Side(new(x1, y0), new(x1, y1)); Side(new(x1, y1), new(x0, y1)); Side(new(x0, y1), new(x0, y0));
        return r;
    }

    static List<Vector2d> Comb(int teeth, double w, double h, double depth)
    {
        var r = new List<Vector2d> { new(0, 0), new(teeth * 2 * w, 0) };
        for (int i = teeth - 1; i >= 0; i--)
        {
            double x = i * 2 * w;
            r.Add(new(x + 2 * w, h)); r.Add(new(x + w, h)); r.Add(new(x + w, h - depth)); r.Add(new(x, h - depth));
        }
        // последняя точка (0, h−depth) — на одной прямой с (0,0) и (0,h) не лежит; добавим (0,h) как коллинеарную вершину
        r.Add(new(0, (h - depth) * 0.5));
        return r;
    }

    static double Area(IReadOnlyList<Vector2d> p)
    {
        double s = 0; var c = p[0];
        for (int i = 0; i < p.Count; i++) { var a = p[i] - c; var b = p[(i + 1) % p.Count] - c; s += a.X * b.Y - a.Y * b.X; }
        return s / 2;
    }

    static (bool ok, string why) Check(List<Vector2d> outer, List<List<Vector2d>> holes, Func<Vector2d, Vector2d> map)
    {
        var o = outer.Select(map).ToList();
        var hs = holes.Select(h => (IReadOnlyList<Vector2d>)h.Select(map).ToList()).ToList();
        double want = Math.Abs(Area(o)) - hs.Sum(h => Math.Abs(Area(h)));
        Triangulator.ForcedClips = 0;
        var tris = Triangulator.Triangulate(o, hs, out var pts);
        if (Environment.GetEnvironmentVariable("TRIDUMP") == "1") { for (int t = 0; t + 2 < tris.Count; t += 3) { var A = pts[tris[t]]; var B = pts[tris[t+1]]; var D = pts[tris[t+2]]; double ar = (B.X-A.X)*(D.Y-A.Y)-(B.Y-A.Y)*(D.X-A.X); Console.WriteLine($"   T{(ar<0?"-":"+")} {tris[t]} {tris[t+1]} {tris[t+2]} {A} {B} {D}"); } }
        // Площади — в локальных координатах (относительно первой точки): иначе у далёких деталей вычитание съедает точность.
        var c = pts.Count > 0 ? pts[0] : Vector2d.Zero;
        double sum = 0, abs = 0, worstNeg = 0;
        for (int t = 0; t + 2 < tris.Count; t += 3)
        {
            Vector2d a = pts[tris[t]] - c, b = pts[tris[t + 1]] - c, d = pts[tris[t + 2]] - c;
            double ar = ((b.X - a.X) * (d.Y - a.Y) - (b.Y - a.Y) * (d.X - a.X)) / 2;
            sum += ar; abs += Math.Abs(ar); worstNeg = Math.Min(worstNeg, ar);
        }
        double tol = 1e-7 * Math.Max(1e-30, want);
        if (Math.Abs(sum - want) > tol) return (false, $"sum {sum:G9} want {want:G9}");
        if (Math.Abs(abs - want) > tol) return (false, $"abs {abs:G9} want {want:G9} (вывернутые/наложенные, худший {worstNeg:G3})");
        return (true, "");
    }

    public static Res Run(bool print)
    {
        int cases = 0, bad = 0; string first = "";
        var shapes = new List<(string name, List<Vector2d> outer, List<List<Vector2d>> holes)>();
        // 1. Плита с решёткой отверстий на общих прямых (r22): k×m квадратных отверстий, стороны с лишними вершинами.
        foreach (var (k, m) in new[] { (2, 2), (3, 3), (4, 2), (5, 5) })
        {
            var holes = new List<List<Vector2d>>();
            for (int i = 0; i < k; i++) for (int j = 0; j < m; j++)
                {
                    var h = Rect(10 + 20 * i, 10 + 20 * j, 20 + 20 * i, 20 + 20 * j, 1); h.Reverse(); holes.Add(h);
                }
            shapes.Add(($"grid{k}x{m}", Rect(0, 0, 20 * k + 10, 20 * m + 10, 3), holes));
        }
        // 2. Отверстия, чьи правые точки лежат на одной горизонтали с вершинами контура и друг с другом.
        {
            var holes = new List<List<Vector2d>>();
            for (int i = 0; i < 6; i++) { var h = Rect(5 + 12 * i, 5, 10 + 12 * i, 10, 2); h.Reverse(); holes.Add(h); }
            shapes.Add(("row6", Rect(0, 0, 80, 15, 8), holes));
        }
        // 3. Гребёнка (много вогнутых вершин, коллинеарные основания зубьев) и гребёнка с отверстиями в основании.
        shapes.Add(("comb20", Comb(20, 1, 10, 6), new()));
        {
            var holes = new List<List<Vector2d>>();
            for (int i = 0; i < 5; i++) { var h = Rect(1 + 8 * i, 1, 3 + 8 * i, 3, 1); h.Reverse(); holes.Add(h); }
            shapes.Add(("comb20h", Comb(20, 1, 10, 6), holes));
        }
        // 4. Тонкая полоса с вершинами почти на прямой (стрелка прогиба 1e-9 доли длины).
        {
            var o = new List<Vector2d>();
            int n = 40;
            for (int i = 0; i <= n; i++) o.Add(new(i * 2.5, (i % 2) * 1e-7));
            for (int i = n; i >= 0; i--) o.Add(new(i * 2.5, 0.5));
            shapes.Add(("sliver", o, new()));
        }

        foreach (var (name, outer, holes) in shapes)
            foreach (double scale in new[] { 0.01, 1.0, 100.0 })
                foreach (double rot in new[] { 0.0, 0.3, Math.PI / 4 })
                    foreach (double off in new[] { 0.0, 3e4, 3e5 })
                    {
                        double c = Math.Cos(rot), s = Math.Sin(rot);
                        Vector2d M(Vector2d p) { p *= scale; return new Vector2d(off + c * p.X - s * p.Y, off * 0.7 + s * p.X + c * p.Y); }
                        cases++;
                        var (ok, why) = Check(outer, holes, M);
                        if (!ok)
                        {
                            bad++;
                            string id = $"{name} s={scale} rot={rot:F2} off={off}: {why}";
                            if (first == "") first = id;
                            if (print) Console.WriteLine("  TRI BAD " + id + " forced=" + Triangulator.ForcedClips);
                        }
                    }
        return new Res(cases, bad, first);
    }
}
