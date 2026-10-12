using OpenTK.Mathematics;
using Parametric3D.Kernel.Geometry;

static partial class Program
{
    static void Krn04UniqChecks()
    {
        Check("KRN04-TRACE-UNIQUE-BRANCH", () =>
        {
            // 04.7: ДВЕ БЛИЗКИЕ ВЕТВИ. Почти горизонтальная плоскость чуть ниже верха тора (R10, r2) пересекает его по двум
            // замкнутым кривым на расстоянии ~2√(2r·ε) друг от друга (0.1–0.25 мм — около двух шагов прослеживания). Прежде
            // ветвь ближе двух шагов к найденной считалась «пояснённой» без доказательства: при ε = 8e-4 принималась одна
            // кривая из двух (длина 63.1 вместо 125.7), при ε = 2e-3 — кривая длиной 26 061 мм. Теперь ячейка у найденной
            // кривой поясняется, только если в её шаре доказана ЕДИНСТВЕННОСТЬ ветви (трансверсальность во всём шаре, шар
            // мал против кривизны), шаг прослеживания — по углу пересечения. Требование: принятый результат — полная длина
            // по независимому счёту (корни по φ на 200 000 значениях θ) до 1e-3; иначе честный отказ (Uncertified).
            // Контроль: плоскость под углом 0.1 у выпуклой части — одна петля, принимается.
            var fails = new List<string>(); int accepted = 0;
            var tor = new Ssi.Torus(Vector3d.Zero, Vector3d.UnitZ, 10, 2);
            foreach (var (eps, tilt) in new[] { (2e-3, 1e-4), (1.5e-3, 3e-5), (8e-4, 3e-5), (5e-4, 1e-4), (3e-3, 1e-4), (1e-2, 3e-5) })
            {
                var n = new Vector3d(Math.Sin(tilt), 0, Math.Cos(tilt)); double d = 10 * Math.Sin(tilt) + 2 - eps;
                var r = Ssi.Intersect(new Ssi.Plane(n * d, n), tor, Vector3d.Zero, 13);
                double len = r.Curves.Sum(c => c.Length);
                // независимо: две ветви — корни g(φ) = sinT(R + r cosφ)cosθ + cosT·r·sinφ − d по обе стороны максимума
                double refLen = 0; var prev = new Vector3d?[2]; int N = 200000;
                for (int i = 0; i <= N; i++)
                {
                    double th = 2 * Math.PI * i / N;
                    double G(double ph) => Math.Sin(tilt) * (10 + 2 * Math.Cos(ph)) * Math.Cos(th) + Math.Cos(tilt) * 2 * Math.Sin(ph) - d;
                    double lo = 0, hi = Math.PI; for (int k = 0; k < 100; k++) { double m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3; if (G(m1) < G(m2)) lo = m1; else hi = m2; }
                    double pm = (lo + hi) / 2;
                    for (int side = 0; side < 2; side++)
                    {
                        double a = side == 0 ? 0 : pm, b = side == 0 ? pm : Math.PI;
                        for (int k = 0; k < 80; k++) { double m = (a + b) / 2; if ((G(m) > 0) == (side == 0)) b = m; else a = m; }
                        double ph = (a + b) / 2; var p = new Vector3d((10 + 2 * Math.Cos(ph)) * Math.Cos(th), (10 + 2 * Math.Cos(ph)) * Math.Sin(th), 2 * Math.Sin(ph));
                        if (prev[side] is { } q) refLen += (p - q).Length; prev[side] = p;
                    }
                }
                bool ok = r.Uncertified != null || Math.Abs(len - refLen) <= 1e-3 * refLen;
                if (r.Uncertified == null) accepted++;
                Log(new { test = "KRN04-UNIQ", eps, tilt, curves = r.Curves.Count, length = len, reference = refLen, uncertified = r.Uncertified });
                if (!ok) fails.Add($"ε {eps} tilt {tilt}: accepted {r.Curves.Count} curve(s) of length {len:G6}, reference {refLen:G6}");
            }
            {
                var n = new Vector3d(Math.Sin(0.1), 0, Math.Cos(0.1)); double d = 10 * Math.Sin(0.1) + 2 - 0.01;
                var r = Ssi.Intersect(new Ssi.Plane(n * d, n), tor, Vector3d.Zero, 13);
                if (r.Uncertified != null || r.Curves.Count != 1) fails.Add($"convex near-tangent loop: {r.Curves.Count} curves, {r.Uncertified}");
            }
            Log(new { test = "KRN04-UNIQ", acceptedCloseBranches = accepted });
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
