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
            // ЛОМАНАЯ СО СКАЧКОМ между ветвями: обе ветви (ε 1e-2, 0.4 мм друг от друга) пройдены, плюс звено «ветвь A → ветвь B».
            // Все точки пересечения пояснены, но звено — не кривая пересечения: удостоверение обязано отказать (звенья — в шаре
            // единственности ветви; дуга в шаре ячейки пройдена до выхода из шара). Без скачка — принимается.
            // Мутация P3D_TRACESTEPCERT=0.
            {
                double tilt = 1e-4, eps = 1e-2, d = 10 * Math.Sin(tilt) + 2 - eps; var nn = new Vector3d(Math.Sin(tilt), 0, Math.Cos(tilt));
                var pl = new Ssi.Plane(nn * d, nn);
                var A = new List<Vector3d>(); var B = new List<Vector3d>(); int N = 6000;
                for (int i = 0; i <= N; i++)
                {
                    double th = 2 * Math.PI * (i % N) / N;
                    double G(double ph) => Math.Sin(tilt) * (10 + 2 * Math.Cos(ph)) * Math.Cos(th) + Math.Cos(tilt) * 2 * Math.Sin(ph) - d;
                    for (int side = 0; side < 2; side++)
                    {
                        double a0 = side == 0 ? 0 : Math.PI / 2, b0 = side == 0 ? Math.PI / 2 : Math.PI;
                        for (int k = 0; k < 100; k++) { double m = (a0 + b0) / 2; if ((G(m) > 0) == (side == 0)) b0 = m; else a0 = m; }
                        double ph = (a0 + b0) / 2; var p = new Vector3d((10 + 2 * Math.Cos(ph)) * Math.Cos(th), (10 + 2 * Math.Cos(ph)) * Math.Sin(th), 2 * Math.Sin(ph));
                        (side == 0 ? A : B).Add(p);
                    }
                }
                string? good = Ssi.CertifyTraceForTest(tor, pl, Vector3d.Zero, 13, new List<List<Vector3d>> { A, B });
                string? jump = Ssi.CertifyTraceForTest(tor, pl, Vector3d.Zero, 13, new List<List<Vector3d>> { A, B, new() { A[1000], B[1000] } });
                Log(new { test = "KRN04-UNIQ", jumpGood = good, jump });
                if (good != null) fails.Add($"two traced branches refused: {good}");
                if (jump == null) fails.Add("a polyline jumping between branches was accepted");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });

        Check("KRN04-NURBS-UNIQUE-BRANCH", () =>
        {
            // 04.7 для граней NURBS: тор (R10, r2) — точная рациональная поверхность 9×9, плоскость почти горизонтальна чуть ниже
            // верха — две близкие замкнутые ветви. Прежде ячейка у найденной кривой пояснялась близостью: принимались «кривая»
            // 6 550 мм (ε 2e-3) и 92.0 мм вместо 125.66 (ε 5e-4); у шва u = 0 / u = 1 кривая обрывалась за шаг до стороны (−3e-4).
            // Теперь (NurbsSsi): единственность ветви в круге у точки ломаной доказывается строгими оценками производных по
            // годографам кусков Безье (G_d ≥ a > 0, |G_e| ≤ a/2), шаг — по углу пересечения, полоса у почти касания исключается
            // по теореме о среднем. Требование: принятый результат — полная длина по независимому счёту (корни по φ) до 2e-5;
            // иначе честный отказ. Обязательно принимаются ε 1e-2 и выпуклая петля (наклон 0.1) — с длиной по аналитическому
            // пути (Ssi плоскость × тор) до 1e-4.
            var fails = new List<string>(); int accepted = 0;
            var S = NurbsTorus(10, 2);
            foreach (var (eps, tilt, must) in new[] { (1e-2, 1e-4, true), (3e-3, 3e-5, false), (2e-3, 1e-4, false), (8e-4, 1e-4, false), (5e-4, 3e-5, false) })
            {
                var n = new Vector3d(Math.Sin(tilt) * Math.Cos(0.785), Math.Sin(tilt) * Math.Sin(0.785), Math.Cos(tilt)); double d = 10 * Math.Sin(tilt) + 2 - eps;
                var r = NurbsSsi.Intersect(S, 0, 1, 0, 1, new Ssi.Plane(n * d, n), 13);
                double len = r.Curves.Sum(c => c.Length);
                // независимо: та же плоскость, повёрнутая к азимуту 0 (тор симметричен) — две ветви, корни по φ
                double refLen = TorusPlaneRefLength(tilt, d, 50000);
                if (r.Uncertified == null) accepted++;
                Log(new { test = "KRN04-NURBS-UNIQ", eps, tilt, curves = r.Curves.Count, length = len, reference = refLen, uncertified = r.Uncertified });
                if (r.Uncertified == null && Math.Abs(len - refLen) > 2e-5 * refLen) fails.Add($"ε {eps} tilt {tilt}: accepted {r.Curves.Count} curve(s) of length {len:G8}, reference {refLen:G8}");
                if (must && r.Uncertified != null) fails.Add($"ε {eps} tilt {tilt}: refused ({r.Uncertified})");
            }
            {
                var n = new Vector3d(Math.Sin(0.1) * Math.Cos(0.785), Math.Sin(0.1) * Math.Sin(0.785), Math.Cos(0.1)); double d = 10 * Math.Sin(0.1) + 2 - 0.01;
                var pl = new Ssi.Plane(n * d, n);
                var r = NurbsSsi.Intersect(S, 0, 1, 0, 1, pl, 13);
                var ra = Ssi.Intersect(pl, new Ssi.Torus(Vector3d.Zero, Vector3d.UnitZ, 10, 2), Vector3d.Zero, 13);
                double len = r.Curves.Sum(c => c.Length), refLen = ra.Curves.Sum(c => c.Length);
                Log(new { test = "KRN04-NURBS-UNIQ", loop = true, length = len, reference = refLen, uncertified = r.Uncertified });
                if (r.Uncertified != null || ra.Uncertified != null || Math.Abs(len - refLen) > 1e-4 * refLen) fails.Add($"convex near-tangent loop: {len:G8} vs {refLen:G8}, {r.Uncertified}");
            }
            // то же для NURBS × NURBS: плоскость — билинейная поверхность NURBS (NurbsNurbsSsi, область (u₁, v₁, u₂, v₂))
            var P0 = new NurbsNurbsSsi.Rect(0, 1, 0, 1); int acceptedNN = 0;
            foreach (var (eps, tilt, must) in new[] { (1e-2, 1e-4, true), (3e-3, 3e-5, false), (2e-3, 1e-4, false), (8e-4, 1e-4, false), (5e-4, 3e-5, false) })
            {
                var n = new Vector3d(Math.Sin(tilt) * Math.Cos(0.785), Math.Sin(tilt) * Math.Sin(0.785), Math.Cos(tilt)); double d = 10 * Math.Sin(tilt) + 2 - eps;
                var r = NurbsNurbsSsi.Intersect(S, P0, NurbsPlane(n * d, n, 15), P0, 13);
                double len = r.Curves.Sum(c => c.Length), refLen = TorusPlaneRefLength(tilt, d, 50000);
                if (r.Uncertified == null) acceptedNN++;
                Log(new { test = "KRN04-NURBS-UNIQ", nn = true, eps, tilt, curves = r.Curves.Count, length = len, reference = refLen, uncertified = r.Uncertified });
                if (r.Uncertified == null && Math.Abs(len - refLen) > 2e-5 * refLen) fails.Add($"NN ε {eps} tilt {tilt}: accepted {r.Curves.Count} curve(s) of length {len:G8}, reference {refLen:G8}");
                if (must && r.Uncertified != null) fails.Add($"NN ε {eps} tilt {tilt}: refused ({r.Uncertified})");
            }
            // ломаная со скачком между ветвями (ε 1e-2): добавлена к найденным — удостоверение обязано отказать (звено не в круге
            // единственности); без неё — принимается. Мутация P3D_NURBSSTEPCERT=0.
            {
                double tilt = 1e-4, eps = 1e-2;
                var n = new Vector3d(Math.Sin(tilt) * Math.Cos(0.785), Math.Sin(tilt) * Math.Sin(0.785), Math.Cos(tilt)); double d = 10 * Math.Sin(tilt) + 2 - eps;
                var pl = new Ssi.Plane(n * d, n);
                var r0 = NurbsSsi.Intersect(S, 0, 1, 0, 1, pl, 13);
                var ca = (NurbsImplicitCurve3)r0.Curves[0]; var cb = (NurbsImplicitCurve3)r0.Curves[1];
                var qa = ca.SeedUV[ca.SeedUV.Count / 3]; var qb = cb.SeedUV.OrderBy(q => (q - qa).Length).First();
                NurbsSsi.TestExtraPolyline = new List<Vector2d> { qa, qb };
                Ssi.Result rj;
                try { rj = NurbsSsi.Intersect(S, 0, 1, 0, 1, pl, 13); } finally { NurbsSsi.TestExtraPolyline = null; }
                // то же для NURBS × NURBS
                var P = NurbsPlane(n * d, n, 15);
                var rn0 = NurbsNurbsSsi.Intersect(S, P0, P, P0, 13);
                var na = (NurbsNurbsCurve3)rn0.Curves[0]; var nb = (NurbsNurbsCurve3)rn0.Curves[1];
                var za = na.Seed[na.Seed.Count / 3]; var zb = nb.Seed.OrderBy(q => (new Vector2d(q.X, q.Y) - new Vector2d(za.X, za.Y)).Length).First();
                NurbsNurbsSsi.TestExtraPolyline = new List<Vector4d> { za, zb };
                Ssi.Result rnj;
                try { rnj = NurbsNurbsSsi.Intersect(S, P0, P, P0, 13); } finally { NurbsNurbsSsi.TestExtraPolyline = null; }
                Log(new { test = "KRN04-NURBS-UNIQ", jump = rj.Uncertified, jumpNN = rnj.Uncertified, good = r0.Uncertified, goodNN = rn0.Uncertified });
                if (r0.Uncertified != null || rn0.Uncertified != null) fails.Add($"two traced branches refused: {r0.Uncertified} {rn0.Uncertified}");
                if (rj.Uncertified == null) fails.Add("NURBS: a polyline jumping between branches was accepted");
                if (rnj.Uncertified == null) fails.Add("NURBS×NURBS: a polyline jumping between branches was accepted");
            }
            Log(new { test = "KRN04-NURBS-UNIQ", acceptedCloseBranches = accepted, acceptedCloseBranchesNN = acceptedNN });
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
