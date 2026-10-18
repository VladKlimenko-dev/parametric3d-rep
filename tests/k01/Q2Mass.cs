using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

/// <summary>
/// m.q2: объём, центр масс и тензор инерции С ГРАНИЦЕЙ ОШИБКИ (MassMeasure) — против замкнутых формул (брусок, цилиндр,
/// шар, конус, тор, плита с отверстием; сдвиги и повороты) и выгрузка для OpenCascade (q2dump → tools/occt_mass.py).
/// </summary>
static partial class Program
{
    /// <summary>Тело формулы: объём, центр масс, тензор инерции в мировой системе.</summary>
    internal sealed record MassCase(string Name, TSolid Body, double V, Vector3d C, Matrix3d J);

    static Matrix3d Diag(double a, double b, double c) => new(a, 0, 0, 0, b, 0, 0, 0, c);

    /// <summary>J мира = R J R^T (R — поворот тела).</summary>
    static Matrix3d Rotate(Matrix3d J, Matrix3d R)
    {
        var r = new double[3, 3] { { R.M11, R.M12, R.M13 }, { R.M21, R.M22, R.M23 }, { R.M31, R.M32, R.M33 } };
        var j = new double[3, 3] { { J.M11, J.M12, J.M13 }, { J.M21, J.M22, J.M23 }, { J.M31, J.M32, J.M33 } };
        var o = new double[3, 3];
        for (int a = 0; a < 3; a++) for (int b = 0; b < 3; b++) { double s = 0; for (int k = 0; k < 3; k++) for (int l = 0; l < 3; l++) s += r[a, k] * j[k, l] * r[b, l]; o[a, b] = s; }
        return new Matrix3d(o[0, 0], o[0, 1], o[0, 2], o[1, 0], o[1, 1], o[1, 2], o[2, 0], o[2, 1], o[2, 2]);
    }

    internal static List<MassCase> MassCases()
    {
        var res = new List<MassCase>();
        // поворот на 33° вокруг (1, 2, 3) и сдвиг (40, −25, 17)
        var axis = new Vector3d(1, 2, 3).Normalized(); double deg = 33;
        var rot = TransformOp.Rotate(Vector3d.Zero, axis, deg);
        var move = new Vector3d(40, -25, 17);
        var x = new TransformOp.Xform(p => rot.Point(p) + move, rot.Direction, false);
        var Rm = new Matrix3d(rot.Direction(Vector3d.UnitX).X, rot.Direction(Vector3d.UnitY).X, rot.Direction(Vector3d.UnitZ).X,
                              rot.Direction(Vector3d.UnitX).Y, rot.Direction(Vector3d.UnitY).Y, rot.Direction(Vector3d.UnitZ).Y,
                              rot.Direction(Vector3d.UnitX).Z, rot.Direction(Vector3d.UnitY).Z, rot.Direction(Vector3d.UnitZ).Z);
        void Add(string name, TSolid local, double V, Vector3d cLocal, Matrix3d JLocal)
        {
            res.Add(new MassCase(name, local, V, cLocal, JLocal));
            var moved = TopologyTransform.Apply(local, x) ?? TransformOp.Copy(local, x);
            res.Add(new MassCase(name + " (rotated, moved)", moved, V, x.Point(cLocal), Rotate(JLocal, Rm)));
        }
        { double a = 60, b = 40, c = 20, V = a * b * c; Add("box 60×40×20", Box(0, 0, 0, a, b, c), V, new(a / 2, b / 2, c / 2), Diag(V * (b * b + c * c) / 12, V * (a * a + c * c) / 12, V * (a * a + b * b) / 12)); }
        { double r = 10, h = 30, V = Math.PI * r * r * h; Add("cylinder r10 h30", PrimitiveOp.Cylinder(Vector3d.Zero, r, h), V, new(0, 0, h / 2), Diag(V * (3 * r * r + h * h) / 12, V * (3 * r * r + h * h) / 12, V * r * r / 2)); }
        { double r = 12, V = 4.0 / 3 * Math.PI * r * r * r; Add("sphere r12", PrimitiveOp.Sphere(Vector3d.Zero, r), V, Vector3d.Zero, Diag(0.4 * V * r * r, 0.4 * V * r * r, 0.4 * V * r * r)); }
        { double r = 10, h = 24, V = Math.PI * r * r * h / 3; Add("cone r10 h24", PrimitiveOp.Cone(Vector3d.Zero, r, 0, h), V, new(0, 0, h / 4), Diag(3.0 / 80 * V * (4 * r * r + h * h), 3.0 / 80 * V * (4 * r * r + h * h), 0.3 * V * r * r)); }
        { double R = 20, r = 6, V = 2 * Math.PI * Math.PI * R * r * r; Add("torus R20 r6", PrimitiveOp.Torus(Vector3d.Zero, R, r), V, Vector3d.Zero, Diag(V * (R * R / 2 + 5.0 / 8 * r * r), V * (R * R / 2 + 5.0 / 8 * r * r), V * (R * R + 0.75 * r * r))); }
        // m.t3: «лимон» и «яблоко» (веретенообразный тор с полюсами в (0, 0, ±4)): эталон — сечениями z = R sin θ (кольцо
        // ρi…ρo, ρ± = A ± R cos θ), Гаусс по θ — независимо от формулы Грина в MassMeasure
        foreach (var (nm, A) in new[] { ("lemon (spindle torus, poles)", -2.0), ("apple (spindle torus, poles)", 1.0) })
        {
            double R = Math.Sqrt(A * A + 16), th4 = Math.Asin(4 / R);
            double V = 0, Jz = 0, Jx = 0;
            void Slice(double t0, double t1, bool annulus)
            {
                double[] gx = { -0.9602898564975363, -0.7966664774136267, -0.5255324099163290, -0.1834346424956498, 0.1834346424956498, 0.5255324099163290, 0.7966664774136267, 0.9602898564975363 };
                double[] gw = { 0.1012285362903763, 0.2223810344533745, 0.3137066458778873, 0.3626837833783620, 0.3626837833783620, 0.3137066458778873, 0.2223810344533745, 0.1012285362903763 };
                for (int k = 0; k < 64; k++)
                {
                    double a0 = t0 + (t1 - t0) * k / 64, a1 = t0 + (t1 - t0) * (k + 1) / 64;
                    for (int q = 0; q < 8; q++)
                    {
                        double th = (a0 + a1) / 2 + (a1 - a0) / 2 * gx[q], wq = gw[q] * (a1 - a0) / 2 * R * Math.Cos(th);
                        double z = R * Math.Sin(th), ro = A + R * Math.Cos(th), ri = annulus ? A - R * Math.Cos(th) : 0;
                        double d2 = ro * ro - ri * ri, d4 = Math.Pow(ro, 4) - Math.Pow(ri, 4);
                        V += wq * Math.PI * d2; Jz += wq * Math.PI / 2 * d4; Jx += wq * (Math.PI / 4 * d4 + Math.PI * d2 * z * z);
                    }
                }
            }
            Slice(-th4, th4, false);
            if (A > 0) { Slice(th4, Math.PI / 2, true); Slice(-Math.PI / 2, -th4, true); }
            Add(nm, Lemon(A), V, Vector3d.Zero, Diag(Jx, Jx, Jz));
        }
        {
            // плита 60×40×20 с отверстием r8 по оси z в точке (20, 20): моменты — разностью (относительно начала)
            double a = 60, b = 40, c = 20, r = 8, Vb = a * b * c, Vc = Math.PI * r * r * c;
            var list = new List<TSolid> { Box(0, 0, 0, a, b, c) };
            BooleanOp.Subtract(list, PrimitiveOp.Cylinder(new Vector3d(20, 20, -5), r, c + 10), new List<string>());
            // ∫ y yᵀ относительно начала: брусок — центральный + Штейнер; цилиндр в (20, 20, 10) высотой 20
            Matrix3d Raw(double V, Vector3d cg, Matrix3d Jc)
            {
                // S = ∫ y yᵀ = (tr(J)/2) I − J + V c cᵀ
                double t = (Jc.M11 + Jc.M22 + Jc.M33) / 2;
                return new Matrix3d(t - Jc.M11 + V * cg.X * cg.X, -Jc.M12 + V * cg.X * cg.Y, -Jc.M13 + V * cg.X * cg.Z,
                                    -Jc.M21 + V * cg.Y * cg.X, t - Jc.M22 + V * cg.Y * cg.Y, -Jc.M23 + V * cg.Y * cg.Z,
                                    -Jc.M31 + V * cg.Z * cg.X, -Jc.M32 + V * cg.Z * cg.Y, t - Jc.M33 + V * cg.Z * cg.Z);
            }
            var cb = new Vector3d(a / 2, b / 2, c / 2); var cc = new Vector3d(20, 20, c / 2);
            var Sb = Raw(Vb, cb, Diag(Vb * (b * b + c * c) / 12, Vb * (a * a + c * c) / 12, Vb * (a * a + b * b) / 12));
            var Sc = Raw(Vc, cc, Diag(Vc * (3 * r * r + c * c) / 12, Vc * (3 * r * r + c * c) / 12, Vc * r * r / 2));
            double V = Vb - Vc; var cg = (cb * Vb - cc * Vc) / V;
            var S = new Matrix3d(Sb.M11 - Sc.M11, Sb.M12 - Sc.M12, Sb.M13 - Sc.M13, Sb.M21 - Sc.M21, Sb.M22 - Sc.M22, Sb.M23 - Sc.M23, Sb.M31 - Sc.M31, Sb.M32 - Sc.M32, Sb.M33 - Sc.M33);
            // центральный: S' = S − V c cᵀ; J = tr(S') I − S'
            var Sp = new Matrix3d(S.M11 - V * cg.X * cg.X, S.M12 - V * cg.X * cg.Y, S.M13 - V * cg.X * cg.Z, S.M21 - V * cg.Y * cg.X, S.M22 - V * cg.Y * cg.Y, S.M23 - V * cg.Y * cg.Z, S.M31 - V * cg.Z * cg.X, S.M32 - V * cg.Z * cg.Y, S.M33 - V * cg.Z * cg.Z);
            double tr = Sp.M11 + Sp.M22 + Sp.M33;
            var J = new Matrix3d(tr - Sp.M11, -Sp.M12, -Sp.M13, -Sp.M21, tr - Sp.M22, -Sp.M23, -Sp.M31, -Sp.M32, tr - Sp.M33);
            Add("plate 60×40×20 with hole r8", list[0], V, cg, J);
        }
        {
            // брусок 80×80×40 минус тор R20 r6 с центром на верхней грани (40, 40, 40): вынута нижняя половина тора —
            // грань тора частичная (точного потока нет: путь по уточнённой сетке). Половина тора: объём V/2, центр масс
            // на 4r/(3π) ниже плоскости, вторые моменты — половина полных (симметрия)
            double a = 80, h = 40, R = 20, r = 6, Vt = 2 * Math.PI * Math.PI * R * r * r, Vh = Vt / 2, Vb = a * a * h;
            var list = new List<TSolid> { Box(0, 0, 0, a, a, h) };
            BooleanOp.Subtract(list, PrimitiveOp.Torus(new Vector3d(40, 40, 40), R, r), new List<string>());
            var cb = new Vector3d(a / 2, a / 2, h / 2); var ch = new Vector3d(40, 40, 40 - 4 * r / (3 * Math.PI));
            Matrix3d RawS(double V, Vector3d cg, double sxx, double syy, double szz) =>     // ∫ y yᵀ от начала по центральным ∫x², ∫y², ∫z² (диагональным)
                new(sxx + V * cg.X * cg.X, V * cg.X * cg.Y, V * cg.X * cg.Z, V * cg.Y * cg.X, syy + V * cg.Y * cg.Y, V * cg.Y * cg.Z, V * cg.Z * cg.X, V * cg.Z * cg.Y, szz + V * cg.Z * cg.Z);
            var Sb = RawS(Vb, cb, Vb * a * a / 12, Vb * a * a / 12, Vb * h * h / 12);
            // половина тора относительно центра тора (40,40,40): ∫x² = ∫y² = Vh(R²/2 + 3r²/8), ∫z² = Vh·r²/4; к своему центру масс — Штейнер по z
            double dz = 4 * r / (3 * Math.PI);
            var Sh = RawS(Vh, ch, Vh * (R * R / 2 + 3 * r * r / 8), Vh * (R * R / 2 + 3 * r * r / 8), Vh * r * r / 4 - Vh * dz * dz);
            double V = Vb - Vh; var cg = (cb * Vb - ch * Vh) / V;
            var S = new Matrix3d(Sb.M11 - Sh.M11, Sb.M12 - Sh.M12, Sb.M13 - Sh.M13, Sb.M21 - Sh.M21, Sb.M22 - Sh.M22, Sb.M23 - Sh.M23, Sb.M31 - Sh.M31, Sb.M32 - Sh.M32, Sb.M33 - Sh.M33);
            var Sp = new Matrix3d(S.M11 - V * cg.X * cg.X, S.M12 - V * cg.X * cg.Y, S.M13 - V * cg.X * cg.Z, S.M21 - V * cg.Y * cg.X, S.M22 - V * cg.Y * cg.Y, S.M23 - V * cg.Y * cg.Z, S.M31 - V * cg.Z * cg.X, S.M32 - V * cg.Z * cg.Y, S.M33 - V * cg.Z * cg.Z);
            double tr = Sp.M11 + Sp.M22 + Sp.M33;
            var J = new Matrix3d(tr - Sp.M11, -Sp.M12, -Sp.M13, -Sp.M21, tr - Sp.M22, -Sp.M23, -Sp.M31, -Sp.M32, tr - Sp.M33);
            Add("box 80×80×40 minus half-buried torus R20 r6", list[0], V, cg, J);
        }
        return res;
    }

    static double MaxAbs(Matrix3d a, Matrix3d b) =>
        new[] { a.M11 - b.M11, a.M12 - b.M12, a.M13 - b.M13, a.M21 - b.M21, a.M22 - b.M22, a.M23 - b.M23, a.M31 - b.M31, a.M32 - b.M32, a.M33 - b.M33 }.Max(Math.Abs);
    static double MaxAbs(Matrix3d a) => MaxAbs(a, Matrix3d.Zero);

    static void Q2MassChecks()
    {
        Check("Q2-MASS-PROPERTIES-BOUNDS", () =>
        {
            var fails = new List<string>();
            foreach (var mc in MassCases())
            {
                if (Environment.GetEnvironmentVariable("Q2FACES") == "1")
                    foreach (var f in mc.Body.Faces) Console.WriteLine($"  {mc.Name}: face {GeometryAudit.AuthoritativeSurface(f)?.GetType().Name} loops {f.Loops.Count} coedges {string.Join(",", f.Loops.Select(l => l.CoEdges.Count))} edges {string.Join(",", f.Loops.SelectMany(l => l.CoEdges).Select(c => c.Edge.Curve.GetType().Name + (c.Edge.V0 == c.Edge.V1 ? "(closed)" : "")))}");
                var m = MassMeasure.Of(mc.Body);
                double dV = Math.Abs(m.Volume - mc.V), dC = (m.Centroid - mc.C).Length, dJ = MaxAbs(m.Inertia, mc.J), J0 = MaxAbs(mc.J);
                bool analytic = m.Route.StartsWith("analytic");
                Log(new { test = "Q2-MASS", name = mc.Name, route = m.Route, dV, eV = m.VolumeError, dC, eC = m.CentroidError, dJ, eJ = m.InertiaError, relJ = m.InertiaError / J0 });
                if (!(dV <= m.VolumeError)) fails.Add($"{mc.Name}: volume off by {dV:G3} > bound {m.VolumeError:G3}");
                if (!(dC <= m.CentroidError)) fails.Add($"{mc.Name}: centroid off by {dC:G3} > bound {m.CentroidError:G3}");
                if (!(dJ <= m.InertiaError)) fails.Add($"{mc.Name}: inertia off by {dJ:G3} > bound {m.InertiaError:G3}");
                // граница содержательна: точный путь — до 1e-5 отн., путь по уточнённой сетке — до 0.1 %
                double lim = analytic ? 1e-5 : 1e-3;
                if (!(m.InertiaError <= lim * J0)) fails.Add($"{mc.Name}: inertia bound {m.InertiaError:G3} is {m.InertiaError / J0:G3} of |J| ({m.Route})");
                if (!(m.VolumeError <= lim * mc.V)) fails.Add($"{mc.Name}: volume bound {m.VolumeError:G3} is {m.VolumeError / mc.V:G3} of V ({m.Route})");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }

    static void Q2Explain(string which)
    {
        MassMeasure.Debug = Console.WriteLine;
        foreach (var (name, build, _) in G14Catalogue().Where(c => c.Name.StartsWith(which)))
        {
            var p = build(); p.Apply(p.Compute(null));
            foreach (var b in p.Bodies)
            {
                Console.WriteLine($"{name}: guarantee {b.Guarantee}, exact authoritative {b.Exact != null && b.ExactAuthoritative}, part volume {p.Volume:R} ± {p.VolumeError:G3}, analytic {AnalyticVolume.Of(b)}");
                Console.WriteLine("  " + MassMeasure.Of(b));
            }
        }
        MassMeasure.Debug = null;
    }

    /// <summary>q2dump: тела формул и каталога g.14 — STEP и наши массовые характеристики с границами (manifest.tsv) для occt_mass.py.</summary>
    static void Q2Dump(string dir)
    {
        Directory.CreateDirectory(dir);
        var man = new List<string> { "id\tname\tV\teV\tcx\tcy\tcz\teC\tJ11\tJ12\tJ13\tJ22\tJ23\tJ33\teJ\troute" };
        int i = 0;
        void Write(string name, IReadOnlyList<TSolid> bodies)
        {
            string id = $"q{i++:D2}";
            if (bodies.Count != 1) return;
            var b = bodies[0];
            var w = new List<string>(); var sw = new StringWriter();
            bool ok = Parametric3D.Kernel.Export.StepExact.Write(sw, new[] { b }, id, w);
            if (!ok) return;
            File.WriteAllText(Path.Combine(dir, id + ".step"), sw.ToString());
            var m = MassMeasure.Of(b);
            var J = m.Inertia;
            man.Add(string.Join("\t", id, name.Replace('\t', ' '), m.Volume.ToString("R"), m.VolumeError.ToString("R"), m.Centroid.X.ToString("R"), m.Centroid.Y.ToString("R"), m.Centroid.Z.ToString("R"),
                m.CentroidError.ToString("R"), J.M11.ToString("R"), J.M12.ToString("R"), J.M13.ToString("R"), J.M22.ToString("R"), J.M23.ToString("R"), J.M33.ToString("R"), m.InertiaError.ToString("R"), m.Route));
        }
        foreach (var mc in MassCases()) Write(mc.Name, new[] { mc.Body });
        foreach (var (name, build, _) in G14Catalogue())
        {
            try { var p = build(); var r = p.Compute(null); p.Apply(r); if (p.Features.Any(f => f.Error != null)) continue; Write(name, p.Bodies); }
            catch (Exception ex) { Console.WriteLine($"{name}: {ex.Message}"); }
        }
        File.WriteAllLines(Path.Combine(dir, "manifest.tsv"), man);
        Console.WriteLine($"q2dump: {man.Count - 1} bodies");
    }
}
