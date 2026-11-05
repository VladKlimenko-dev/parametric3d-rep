using OpenTK.Mathematics;
using Parametric3D.Kernel.Ops;

static partial class Program
{
    /// <summary>
    /// k165 (UI-ROUND-RIM-BALL): РЕЗ ПО СЕТКАМ ПРИ КАСАНИИ ПО ВЕРШИНАМ.
    /// KRN-MESHCUT-TOUCH-K165 — куб 10 мм, грани y = 0, 10 и z = 0, 10 разбиты на две половины по x = 5; резец — коробка
    /// [5, 15] × [−1, 11] × [−1, 11]: его плоскость x = 5 проходит по рёбрам сетки куба, не разрезая ни одного многоугольника.
    /// Прежде нетронутые многоугольники куба связывались в одну область через вершины x = 5 и получали один ответ — рез
    /// выходил незамкнутым (или пустым). Теперь вершина на поверхности резца не связывает области: объём 500 (половина),
    /// сетка замкнута. Мутация P3D_MESHCUTREGIONTOUCH=0 — падает.
    /// KRN-MESHCUT-FLIPCOPY-K165 — вход резца после реза не меняется (перевёрнутые куски — копии). Мутация P3D_MESHCUTFLIPCOPY=0.
    /// </summary>
    static void MeshCut165Checks()
    {
        static CsgPolygon Q(Vector3d a, Vector3d b, Vector3d c, Vector3d d, uint tag) => new(new List<Vector3d> { a, b, c, d }, tag);
        static List<CsgPolygon> Box(Vector3d lo, Vector3d hi, uint tag, double? splitX = null)
        {
            var r = new List<CsgPolygon>();
            double[] xs = splitX is { } sx ? new[] { lo.X, sx, hi.X } : new[] { lo.X, hi.X };
            for (int i = 0; i + 1 < xs.Length; i++)
            {
                double x0 = xs[i], x1 = xs[i + 1];
                // низ z = lo (нормаль −z), верх z = hi, перед y = lo (−y), зад y = hi
                r.Add(Q(new(x0, lo.Y, lo.Z), new(x0, hi.Y, lo.Z), new(x1, hi.Y, lo.Z), new(x1, lo.Y, lo.Z), tag));
                r.Add(Q(new(x0, lo.Y, hi.Z), new(x1, lo.Y, hi.Z), new(x1, hi.Y, hi.Z), new(x0, hi.Y, hi.Z), tag));
                r.Add(Q(new(x0, lo.Y, lo.Z), new(x1, lo.Y, lo.Z), new(x1, lo.Y, hi.Z), new(x0, lo.Y, hi.Z), tag));
                r.Add(Q(new(x0, hi.Y, lo.Z), new(x0, hi.Y, hi.Z), new(x1, hi.Y, hi.Z), new(x1, hi.Y, lo.Z), tag));
            }
            r.Add(Q(new(lo.X, lo.Y, lo.Z), new(lo.X, lo.Y, hi.Z), new(lo.X, hi.Y, hi.Z), new(lo.X, hi.Y, lo.Z), tag));
            r.Add(Q(new(hi.X, lo.Y, lo.Z), new(hi.X, hi.Y, lo.Z), new(hi.X, hi.Y, hi.Z), new(hi.X, lo.Y, hi.Z), tag));
            return r;
        }
        static (double Vol, double Va, double Area) Sums(List<CsgPolygon> ps)
        {
            double vol = 0, area = 0; var va = Vector3d.Zero;
            foreach (var p in ps)
                for (int i = 1; i + 1 < p.V.Count; i++)
                {
                    var cr = Vector3d.Cross(p.V[i] - p.V[0], p.V[i + 1] - p.V[0]);
                    va += cr / 2; area += cr.Length / 2;
                    vol += Vector3d.Dot(p.V[0], Vector3d.Cross(p.V[i], p.V[i + 1])) / 6;
                }
            return (vol, va.Length, area);
        }

        Check("KRN-MESHCUT-TOUCH-K165", () =>
        {
            var a = Box(new(0, 0, 0), new(10, 10, 10), 1, splitX: 5);
            var t = Box(new(5, -1, -1), new(15, 11, 11), 2);
            var (v0, va0, _) = Sums(a);
            var r = MeshCut.Run(a, t, subtract: true) ?? new();
            var (v, va, ar) = Sums(r);
            Log(new { test = "KRN-MESHCUT-TOUCH-K165", input = Math.Round(v0, 6), inputVa = va0, pieces = r.Count, volume = Math.Round(v, 6), vectorArea = va, area = Math.Round(ar, 6) });
            Require(Math.Abs(v0 - 1000) < 1e-9 && va0 < 1e-9, "input cube");
            Require(va < 1e-6 * Math.Max(1, ar), $"the cut is open: |vector area| {va:G4}");
            Require(Math.Abs(v - 500) < 1e-6, $"volume {v:0.######}, want 500");
            Require(Math.Abs(ar - 400) < 1e-6, $"area {ar:0.######}, want 400 (half cube)");
        });

        Check("KRN-MESHCUT-FLIPCOPY-K165", () =>
        {
            // резец внутри куба (полость): его многоугольники остаются перевёрнутыми — во входе они не должны перевернуться
            var a = Box(new(0, 0, 0), new(10, 10, 10), 1);
            var t = Box(new(3, 3, 3), new(7, 7, 7), 2);
            var before = t.Select(p => (p.N, p.V[0], p.V[1])).ToList();
            var r = MeshCut.Run(a, t, subtract: true) ?? new();
            var (v, va, _) = Sums(r);
            int changed = t.Select((p, i) => (p.N - before[i].N).Length > 1e-12 || p.V[0] != before[i].Item2 || p.V[1] != before[i].Item3).Count(x => x);
            Log(new { test = "KRN-MESHCUT-FLIPCOPY-K165", volume = Math.Round(v, 6), vectorArea = va, changedInputs = changed });
            Require(Math.Abs(v - (1000 - 64)) < 1e-6 && va < 1e-9, $"cube with a cavity: {v:0.######}");
            Require(changed == 0, $"cutter polygons changed by the cut: {changed}");
        });
    }
}
