using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;

static partial class Program
{
    /// <summary>
    /// k176: ШЕСТЕРНЯ ЭСКИЗА (SketchGear.cs). Наружный и внутренний эвольвентный венец, циклоидальный и прямобочный зуб, переходная
    /// кривая и скругление вершины. Норма: контур замкнут (соседние элементы делят точки), радиусы вершин и впадин — по формулам,
    /// толщина зуба по делительной окружности — m(π/2 ± 2x·tgα) − зазор, стороны отходят от эвольвенты не больше допуска,
    /// переходные дуги касательны к стороне; объём выдавливания — площадь профиля, посчитанная отдельно по формуле h(ρ), × высота;
    /// запись и чтение файла сохраняют параметры (мутация P3D_SKGEARSAVE=0 — проверка падает), снимок отмены — тоже.
    /// </summary>
    static void SkGearChecks()
    {
        Check("SK-GEAR-K176", () =>
        {
            static double Inv(double a) => Math.Tan(a) - a;
            // половинная угловая толщина эвольвентного зуба наружного колеса (ниже основной окружности — радиальная)
            static double Hext(GearParams p, double s, double rho)
            {
                double r = p.PitchRadius, rb = p.BaseRadius;
                return s / (2 * r) + Inv(p.Alpha) - Inv(Math.Acos(Math.Min(1, rb / Math.Max(rho, rb))));
            }
            static double Simpson(Func<double, double> f, double a, double b, int n = 20000)
            {
                double h = (b - a) / n, sum = f(a) + f(b);
                for (int i = 1; i < n; i++) sum += f(a + i * h) * (i % 2 == 1 ? 4 : 2);
                return sum * h / 3;
            }
            static Vector2d Bez(List<Vector2d> cp, double t)
            {
                var tmp = cp.ToArray();
                for (int k = tmp.Length - 1; k > 0; k--) for (int i = 0; i < k; i++) tmp[i] = tmp[i] * (1 - t) + tmp[i + 1] * t;
                return tmp[0];
            }
            static Vector2d SegAt(GearGeometry.Seg s, double t)
            {
                switch (s.Kind)
                {
                    case GearGeometry.SegKind.Line: return s.Start * (1 - t) + s.End * t;
                    case GearGeometry.SegKind.Arc:
                    {
                        var u = s.Start - s.C; var v = s.End - s.C; double a0 = Math.Atan2(u.Y, u.X), sw = Math.Atan2(v.Y, v.X) - a0;
                        if (s.Ccw) { while (sw <= 0) sw += 2 * Math.PI; } else { while (sw >= 0) sw -= 2 * Math.PI; }
                        return s.C + new Vector2d(Math.Cos(a0 + sw * t), Math.Sin(a0 + sw * t)) * u.Length;
                    }
                    default: return Bez(s.P, t);
                }
            }
            static Vector2d TanAt(GearGeometry.Seg s, double t) { double d = 1e-7; var a = SegAt(s, Math.Max(0, t - d)); var b = SegAt(s, Math.Min(1, t + d)); return (b - a).Normalized(); }

            var problems = new List<string>();
            var report = new List<object>();

            (List<GearGeometry.Seg> Segs, double MinR, double MaxR, double Gap, double Kink) Inspect(string name, GearParams p)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var segs = GearGeometry.Build(p, out var why) ?? throw new Exception($"{name}: not built ({why})");
                double gap = 0, kink = 0, minR = double.MaxValue, maxR = 0;
                var c = new Vector2d(p.CenterX, p.CenterY);
                for (int i = 0; i < segs.Count; i++)
                {
                    var a = segs[i]; var b = segs[(i + 1) % segs.Count];
                    gap = Math.Max(gap, (a.End - b.Start).Length);
                    // излом касательной на стыке переходной дуги / скругления вершины со стороной
                    bool fillet = (a.Kind == GearGeometry.SegKind.Arc && (a.C - c).Length > 1e-9) || (b.Kind == GearGeometry.SegKind.Arc && (b.C - c).Length > 1e-9);
                    if (fillet && a.Kind != b.Kind) kink = Math.Max(kink, Vector2d.Dot(TanAt(a, 1), TanAt(b, 0)) is var dt ? Math.Acos(Math.Clamp(dt, -1, 1)) : 0);
                    for (int k = 0; k <= 50; k++) { double rr = (SegAt(a, k / 50.0) - c).Length; minR = Math.Min(minR, rr); maxR = Math.Max(maxR, rr); }
                }
                report.Add(new { gear = name, segs = segs.Count, ms = sw.ElapsedMilliseconds, gap, kink, minR, maxR, ra = p.TipRadius, rf = p.RootRadius });
                if (gap > 1e-9) problems.Add($"{name}: contour gap {gap:E2}");
                return (segs, minR, maxR, gap, kink);
            }

            // толщина зуба по делительной окружности: угловое расстояние между точками профиля на окружности r у первого зуба
            double ThicknessAtPitch(GearParams p, List<GearGeometry.Seg> segs)
            {
                double r = p.PitchRadius, rot = p.Rotation * Math.PI / 180; var c = new Vector2d(p.CenterX, p.CenterY);
                var angs = new List<double>();
                foreach (var s in segs)
                {
                    double F(double t) => (SegAt(s, t) - c).Length - r;
                    const int N = 64;
                    for (int k = 0; k < N; k++)
                    {
                        double t0 = k / (double)N, t1 = (k + 1) / (double)N, f0 = F(t0), f1 = F(t1);
                        if (f0 * f1 > 0 || (f0 == 0 && f1 == 0)) continue;
                        for (int it = 0; it < 80; it++) { double tm = (t0 + t1) / 2; if ((F(tm) < 0) == (f0 < 0)) { t0 = tm; f0 = F(tm); } else t1 = tm; }
                        var q = SegAt(s, (t0 + t1) / 2) - c; double ang = Math.Atan2(q.Y, q.X) - rot;
                        while (ang > Math.PI) ang -= 2 * Math.PI; while (ang < -Math.PI) ang += 2 * Math.PI;
                        if (Math.Abs(ang) < Math.PI / p.Teeth) angs.Add(ang);
                    }
                }
                angs = angs.Distinct().OrderBy(x => x).ToList();
                if (angs.Count < 2) return double.NaN;
                double half = Math.Min(Math.Abs(angs.First()), Math.Abs(angs.Last()));
                // у наружного зуб — между точками ±ψ; у внутреннего — тоже зуб (ось зуба — по оси поворота)
                return 2 * half * r;
            }

            double FlankDeviation(GearParams p, List<GearGeometry.Seg> segs, double sExt)
            {
                double worst = 0, rb = p.BaseRadius, rot = p.Rotation * Math.PI / 180; var c = new Vector2d(p.CenterX, p.CenterY);
                foreach (var s in segs.Where(x => x.Kind == GearGeometry.SegKind.Bezier))
                    for (int k = 0; k <= 40; k++)
                    {
                        var q = SegAt(s, k / 40.0) - c; double rho = q.Length; if (rho < rb) continue;
                        double ang = Math.Atan2(q.Y, q.X) - rot; double pitch = 2 * Math.PI / p.Teeth;
                        ang -= pitch * Math.Round(ang / pitch);
                        double want = p.Internal ? Math.PI / p.Teeth - Hext(p, sExt, rho) : Hext(p, sExt, rho);
                        worst = Math.Max(worst, Math.Abs(Math.Abs(ang) - want) * rho);
                    }
                return worst;
            }

            double ExtrudedVolume(Sketch sk, out string? err)
            {
                var part = new Part("g"); part.Add(sk);
                var ex = new ExtrudeFeature { SketchId = sk.Id }; ex.Side1.Distance = new ValueRef(10); part.AddFeature(ex);
                part.Rebuild(null); err = ex.Error;
                return part.Volume;
            }

            // 1. наружный эвольвентный, без переходной кривой — объём по формуле площади
            var e1 = new GearParams { Teeth = 20, Module = 2, RootFilletCoef = 0, Tolerance = 1e-5, PitchCircle = false, Rotation = 7 };
            var i1 = Inspect("external z20 m2", e1);
            if (Math.Abs(i1.MaxR - e1.TipRadius) > 1e-6 || Math.Abs(i1.MinR - e1.RootRadius) > 1e-6) problems.Add($"external radii {i1.MinR}..{i1.MaxR}");
            double s1 = ThicknessAtPitch(e1, i1.Segs), dev1 = FlankDeviation(e1, i1.Segs, e1.ToothThickness);
            if (Math.Abs(s1 - e1.ToothThickness) > 2e-5) problems.Add($"external tooth thickness {s1} want {e1.ToothThickness}");
            if (dev1 > 1.01e-5) problems.Add($"external flank deviation {dev1:E2}");
            double area1 = Math.PI * e1.RootRadius * e1.RootRadius + e1.Teeth * Simpson(rho => 2 * Hext(e1, e1.ToothThickness, rho) * rho, e1.RootRadius, e1.TipRadius);
            var sk1 = new Sketch("g1", Frame3.XY); sk1.AddGear(e1, out _);
            double v1 = ExtrudedVolume(sk1, out var err1);
            report.Add(new { test = "external volume", v1, want = 10 * area1, err1 });
            if (err1 != null || Math.Abs(v1 - 10 * area1) > 1e-4 * 10 * area1) problems.Add($"external extrude {v1} want {10 * area1} ({err1})");

            // 2. внутренний эвольвентный z40, смещение 0.2 — объём «колеса впадин» по формуле
            var e2 = new GearParams { Internal = true, Teeth = 40, Module = 2, ProfileShift = 0.2, RootFilletCoef = 0, Tolerance = 1e-5, PitchCircle = false };
            var i2 = Inspect("internal z40 m2 x0.2", e2);
            if (Math.Abs(i2.MinR - e2.TipRadius) > 1e-6 || Math.Abs(i2.MaxR - e2.RootRadius) > 1e-6) problems.Add($"internal radii {i2.MinR}..{i2.MaxR}");
            double s2 = ThicknessAtPitch(e2, i2.Segs);
            if (Math.Abs(s2 - e2.ToothThickness) > 2e-5) problems.Add($"internal tooth thickness {s2} want {e2.ToothThickness}");
            double sExt2 = Math.PI * e2.Module - e2.ToothThickness;
            double dev2 = FlankDeviation(e2, i2.Segs, sExt2);
            if (dev2 > 1.01e-5) problems.Add($"internal flank deviation {dev2:E2}");
            double area2 = Math.PI * e2.TipRadius * e2.TipRadius + e2.Teeth * Simpson(rho => 2 * Hext(e2, sExt2, rho) * rho, e2.TipRadius, e2.RootRadius);
            var sk2 = new Sketch("g2", Frame3.XY); sk2.AddGear(e2, out _);
            double v2 = ExtrudedVolume(sk2, out var err2);
            report.Add(new { test = "internal volume", v2, want = 10 * area2, err2 });
            if (err2 != null || Math.Abs(v2 - 10 * area2) > 1e-4 * 10 * area2) problems.Add($"internal extrude {v2} want {10 * area2} ({err2})");

            // 3. переходная кривая и скругление вершины — касание, тело строится
            var e3 = new GearParams { Teeth = 17, Module = 3, TipRoundCoef = 0.1, CenterX = 15, CenterY = -4, Rotation = 3 };
            var i3 = Inspect("external z17 fillets", e3);
            if (i3.Kink > 1e-5) problems.Add($"fillet tangency kink {i3.Kink:E2}");
            var sk3 = new Sketch("g3", Frame3.XY); sk3.AddGear(e3, out _);
            double v3 = ExtrudedVolume(sk3, out var err3);
            if (err3 != null || !(v3 > 0)) problems.Add($"filleted gear extrude {v3} ({err3})");

            // 4. циклоидальный и прямобочный; внутренний циклоидальный
            foreach (var (nm, gp) in new[]
            {
                ("cycloidal z30", new GearParams { Profile = GearProfile.Cycloidal, Teeth = 30, Module = 1 }),
                ("cycloidal internal z48", new GearParams { Profile = GearProfile.Cycloidal, Internal = true, Teeth = 48, Module = 1, RollingCircleCoef = 3 }),
                ("straight z12", new GearParams { Profile = GearProfile.StraightFlank, Teeth = 12, Module = 4, PressureAngle = 15 }),
                ("straight internal z24", new GearParams { Profile = GearProfile.StraightFlank, Internal = true, Teeth = 24, Module = 2, PressureAngle = 10 }),
                ("involute z200", new GearParams { Teeth = 200, Module = 0.5 }),
            })
            {
                var ii = Inspect(nm, gp);
                var sk = new Sketch(nm, Frame3.XY); sk.AddGear(gp, out _);
                double vv = ExtrudedVolume(sk, out var ee);
                report.Add(new { gear = nm, volume = vv, error = ee });
                if (ee != null || !(vv > 0)) problems.Add($"{nm}: extrude {vv} ({ee})");
                if (gp.Profile == GearProfile.StraightFlank && ii.Segs.Any(x => x.Kind == GearGeometry.SegKind.Bezier)) problems.Add($"{nm}: straight flank as curves");
            }

            // 5. запись и чтение файла; снимок отмены
            {
                var part = new Part("rt"); var sk = new Sketch("S", Frame3.XY); part.Add(sk);
                var g = sk.AddGear(new GearParams { Teeth = 23, Module = 1.5, ProfileShift = 0.3, Backlash = 0.05, Rotation = 11, CenterX = 5 }, out _)!;
                var ex = new ExtrudeFeature { SketchId = sk.Id }; ex.Side1.Distance = new ValueRef(8); part.AddFeature(ex);
                part.Rebuild(null); double v0 = part.Volume;
                var (r, rp) = DocumentFile.Load(DocumentFile.Save(part, null)); r.Rebuild(rp);
                var rsk = r.Sketches.Single(); var rg = rsk.Gears.SingleOrDefault();
                report.Add(new { test = "file", gears = rsk.Gears.Count, teeth = rg?.P.Teeth, v0, back = r.Volume, items = rg?.Items.Count });
                if (rg == null || rg.P.Teeth != 23 || Math.Abs(rg.P.ProfileShift - 0.3) > 1e-12 || Math.Abs(rg.P.Backlash - 0.05) > 1e-12 || rg.Items.Count != g.Items.Count
                    || rg.Items.Any(id => rsk.Entities.All(e => e.Id != id)))
                    problems.Add("gear parameters lost in the file");
                else
                {
                    if (Math.Abs(r.Volume - v0) > 1e-9 * v0) problems.Add($"file volume {r.Volume} vs {v0}");
                    rg.P.Teeth = 25; if (!rsk.Regenerate(rg, out var wr)) problems.Add("regenerate after load: " + wr);
                    if (rsk.Entities.Count(e => rg.Items.Contains(e.Id)) != rg.Items.Count) problems.Add("regenerated items");
                }
                var st = SketchState.Capture(sk); int n0 = sk.Entities.Count;
                g.P.Teeth = 30; sk.Regenerate(g, out _);
                st.RestoreInto(sk);
                if (sk.Gears.Single().P.Teeth != 23 || sk.Entities.Count != n0) problems.Add("undo snapshot lost the gear");
                // недопустимые параметры — отказ без изменения
                var bad = new GearParams { Teeth = 8, Module = 2, ProfileShift = -3 };
                if (sk.AddGear(bad, out var wb) != null) problems.Add("bad parameters accepted"); else report.Add(new { test = "bad", why = wb });
            }

            Log(new { test = "SK-GEAR-K176", report, problems });
            Require(problems.Count == 0, string.Join("; ", problems));
        });
    }
}
