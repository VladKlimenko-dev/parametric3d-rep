using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Params;

static partial class Program
{
    /// <summary>
    /// m.x4: ЕДИНИЦЫ. Величины с размерностью (длина, площадь, объём, масса, плотность, время, угол, сила, давление, энергия),
    /// точные множители, ввод с единицами и проверка согласованности, параметры с единицами, система единиц документа и
    /// пользователя, детали в разных единицах в одной модели без потери точности, явные единицы STEP и IGES при чтении.
    /// </summary>
    static void UnitsChecks()
    {
        Check("UNITS-CONVERSIONS-EXACT", () =>
        {
            var fails = new List<string>();
            void Eq(string name, double got, double want, double rel = 1e-15) { if (!(Math.Abs(got - want) <= rel * Math.Max(1, Math.Abs(want)))) fails.Add($"{name}: {got:R} vs {want:R}"); }
            Eq("1 in → mm", Units.Convert(1, "in", "mm"), 25.4);
            Eq("1 ft → in", Units.Convert(1, "ft", "in"), 12);
            Eq("1 mi → km", Units.Convert(1, "mi", "km"), 1.609344);
            Eq("1 lb → kg", Units.Convert(1, "lb", "kg"), 0.45359237);
            Eq("1 g/cm^3 → kg/m^3", Units.Convert(1, "g/cm^3", "kg/m^3"), 1000, 1e-13);
            Eq("1 lb/in^3 → kg/m^3", Units.Convert(1, "lb/in^3", "kg/m^3"), 0.45359237 / Math.Pow(0.0254, 3), 1e-13);
            Eq("1 psi → Pa", Units.Convert(1, "psi", "Pa"), 6894.757293168361, 1e-13);
            Eq("1 MPa → N/mm^2", Units.Convert(1, "MPa", "N/mm^2"), 1, 1e-13);
            Eq("1 kg*m/s^2 → N", Units.Convert(1, "kg*m/s^2", "N"), 1, 1e-13);
            Eq("1 N·m → J", Units.Convert(1, "N·m", "J"), 1, 1e-13);
            Eq("1 rad → deg", Units.Convert(1, "rad", "deg"), 180 / Math.PI);
            Eq("1 h → s", Units.Convert(1, "h", "s"), 3600);
            Eq("1 L → mm³", Units.Convert(1, "L", "mm^3"), 1e6);
            Eq("1 m² → mm²", Units.Convert(1, "m²", "mm^2"), 1e6);
            // туда и обратно — без потери
            foreach (var u in new[] { "in", "ft", "um", "mil", "lb", "oz", "psi", "lbf", "g/cm^3", "deg", "rad" })
            {
                var x = Units.Parse(u); double v = 123.456789; Eq($"round trip {u}", x.FromInternal(x.ToInternal(v)), v, 1e-15);
            }
            bool threw = false; try { Units.Convert(1, "mm", "kg"); } catch (InvalidOperationException) { threw = true; }
            if (!threw) fails.Add("mm → kg did not refuse");
            threw = false; try { Units.Parse("furlong"); } catch (FormatException) { threw = true; }
            if (!threw) fails.Add("unknown unit accepted");
            Require(fails.Count == 0, string.Join("; ", fails));
        });

        Check("UNITS-EXPRESSIONS", () =>
        {
            var fails = new List<string>();
            var t = new ParameterTable();
            t.Add("h", 7);                                      // параметр с именем, совпадающим с единицей часа
            void Q(string f, double want, Dimension dim, double rel = 1e-14)
            {
                if (!t.TryEvaluateQuantity(f, out var q, out var err)) { fails.Add($"'{f}': {err}"); return; }
                if (q.Dim != dim) fails.Add($"'{f}': {q.Dim} vs {dim}");
                else if (!(Math.Abs(q.Value - want) <= rel * Math.Max(1, Math.Abs(want)))) fails.Add($"'{f}': {q.Value:R} vs {want:R}");
                // то же значение — и без разбора размерности (внутренние единицы)
                if (t.TryEvaluate(f, out double v, out _) && !(Math.Abs(v - q.Value) <= rel * Math.Max(1, Math.Abs(want)))) fails.Add($"'{f}': Eval {v:R} vs quantity {q.Value:R}");
            }
            void Bad(string f) { if (t.TryEvaluateQuantity(f, out _, out _)) fails.Add($"'{f}' accepted"); }
            Q("10 in + 5 mm", 259, Dimension.Length);
            Q("10in+5mm", 259, Dimension.Length);
            Q("2 in * 3 in", 6 * 25.4 * 25.4, Dimension.Area);
            Q("sqrt(16 mm^2)", 4, Dimension.Length);
            Q("1 m / 2", 500, Dimension.Length);
            Q("sin(30 deg)", 0.5, Dimension.None);
            Q("sin(30°)", 0.5, Dimension.None);
            Q("sin(0.5235987755982988 rad)", 0.5, Dimension.None, 1e-12);
            Q("sin(30)", 0.5, Dimension.None);                  // число — градусы, как прежде
            Q("atan2(1 in, 25.4 mm)", 45, Dimension.Angle);
            Q("7.85 g/cm^3", 7.85e-6, Dimension.Density, 1e-13);
            Q("2 h", 7200, Dimension.Time);                     // единица — сразу за числом
            Q("2*h", 14, Dimension.None);                       // параметр h
            Q("10 in + 5", 259, Dimension.Length);              // число без единицы — в единице документа (мм)
            Q("max(1 in, 20 mm)", 25.4, Dimension.Length);
            Q("(3 + 4) * 2", 14, Dimension.None);               // без единиц — как прежде
            Bad("10 mm + 5 kg");
            Bad("sin(5 mm)");
            Bad("sqrt(5 mm)");
            Bad("2 ^ (3 mm)");
            Bad("10 mm + 2 deg");
            {
                // 100 Н / 100 мм² = 1 Н/мм² = 1 МПа
                t.TryEvaluateQuantity("100 N / (10 mm * 10 mm)", out var q, out _);
                double mpa = Units.Parse("MPa").FromInternal(q.Value); if (Math.Abs(mpa - 1) > 1e-13) fails.Add($"pressure in MPa {mpa:R}");
            }
            // документ в дюймах: число без единицы рядом с длиной — дюймы
            var ti = new ParameterTable { Units = UnitSystem.Imperial };
            if (!ti.TryEvaluateQuantity("10 in + 5", out var qi, out var e2) || Math.Abs(qi.Value - 381) > 1e-12) fails.Add($"imperial '10 in + 5': {qi.Value} {e2}");
            Require(fails.Count == 0, string.Join("; ", fails));
        });

        Check("UNITS-PARAMETERS-AND-DOCUMENT", () =>
        {
            var fails = new List<string>();
            var part = new Part("inch part") { Units = UnitSystem.Imperial };
            var t = new ParameterTable { Units = part.Units };
            var L = t.AddQuantity("L", "2 in");
            if (Math.Abs(L.Value - 50.8) > 1e-12 || L.Unit != "in") fails.Add($"L = {L.Value} {L.Unit}");
            var W = t.AddFormula("W", "L * 2", "in");
            var B = t.AddFormula("B", "3", "in");               // число без единицы в параметре с единицей — в его единице
            var M = t.AddFormula("M", "5 kg", "in");            // несогласованная размерность — ошибка параметра
            var A = t.AddFormula("A", "L * W", "in^2");
            t.Recalculate();
            if (W.Error != null || Math.Abs(W.Value - 101.6) > 1e-12) fails.Add($"W = {W.Value} {W.Error}");
            if (B.Error != null || Math.Abs(B.Value - 76.2) > 1e-12) fails.Add($"B = {B.Value} {B.Error}");
            if (M.Error == null) fails.Add("M (5 kg as inches) accepted");
            if (A.Error != null || Math.Abs(A.Value - 50.8 * 101.6) > 1e-9) fails.Add($"A = {A.Value} {A.Error}");
            // показ: в системе документа и в системе пользователя — модель та же
            string inDoc = Units.Format(W.Quantity, part.Units), inUser = Units.Format(W.Quantity, UnitSystem.Metric);
            if (inDoc != "4 in" || inUser != "101.6 mm") fails.Add($"display '{inDoc}' / '{inUser}'");
            if (Units.Format(new Quantity(7.85e-6, Dimension.Density), UnitSystem.Imperial) != "0.2836 lb/in^3") fails.Add("density display " + Units.Format(new Quantity(7.85e-6, Dimension.Density), UnitSystem.Imperial));
            // сохранение и чтение: система единиц и параметры с единицами
            TestGeometry.AddCube(part, 2 * 25.4);
            part.Rebuild(t);
            string json = DocumentFile.Save(part, t);
            var (p2, t2) = DocumentFile.Load(json);
            if (p2.Units.Length != "in" || p2.Units.Mass != "lb" || t2.Units.Length != "in") fails.Add($"units after load: {p2.Units.Length}/{p2.Units.Mass}/{t2.Units.Length}");
            if (t2.Find("W") is not { } w2 || Math.Abs(w2.Value - 101.6) > 1e-12 || w2.Unit != "in") fails.Add("W after load");
            {
                // ид. эскизов при чтении выдаются заново — сравнивается то, что относится к единицам и параметрам
                var j2 = DocumentFile.Save(p2, t2);
                string Head(string j) => j[..j.IndexOf("\"Sketches\"", StringComparison.Ordinal)];
                if (Head(j2) != Head(json)) fails.Add("save → load → save: units or parameters differ");
            }
            // деталь в дюймах и деталь в мм: геометрия — во внутренних единицах, одна и та же до бита
            var pm = new Part("mm part"); TestGeometry.AddCube(pm, 2 * 25.4); pm.Rebuild(null);
            p2.Rebuild(t2);
            if (pm.Volume != p2.Volume) fails.Add($"volume inch-doc {p2.Volume:R} vs mm-doc {pm.Volume:R}");
            if (MassSummary.Of(pm).Mass != MassSummary.Of(p2).Mass) fails.Add("mass differs between unit systems");
            // сборка в метрах из детали в дюймах и детали в мм: каждая хранит свою систему; после записи и чтения — те же
            {
                var asm = new Assembly("mixed") { Units = UnitSystem.MetricMeters };
                asm.Add(p2); asm.Add(pm);
                var (a2, _) = AssemblyFile.Load(AssemblyFile.Save(asm));
                var ps = a2.Parts.ToList();
                if (a2.Units.Length != "m") fails.Add("assembly units after load " + a2.Units.Length);
                if (ps.Count != 2 || ps[0].Units.Length != "in" || ps[1].Units.Length != "mm") fails.Add("part units in the assembly: " + string.Join(",", ps.Select(x => x.Units.Length)));
                foreach (var x in ps) x.Rebuild(null);
                if (ps.Count == 2 && ps[0].Volume != ps[1].Volume) fails.Add($"assembly part volumes {ps[0].Volume:R} vs {ps[1].Volume:R}");
            }
            // недопустимая единица в файле — ошибка чтения с причиной
            bool threw = false; try { DocumentFile.Load(json.Replace("\"in\"", "\"furlong\"")); } catch (InvalidDataException) { threw = true; }
            if (!threw) fails.Add("unknown unit in the file was accepted");
            Require(fails.Count == 0, string.Join("; ", fails));
        });

        Check("UNITS-STEP-IGES-READ", () =>
        {
            var fails = new List<string>();
            var box = Box(0, 0, 0, 10, 20, 30); double v0 = 6000;
            // STEP: файл в дюймах (единица контекста — дюйм через миллиметр)
            var sw = new StringWriter(); Parametric3D.Kernel.Export.StepExact.Write(sw, new[] { box }, "box", new List<string>());
            string step = sw.ToString();
            string inch = step.Replace("(LENGTH_UNIT()NAMED_UNIT(*)SI_UNIT(.MILLI.,.METRE.))",
                "(CONVERSION_BASED_UNIT('INCH',#990001)LENGTH_UNIT()NAMED_UNIT(*));\n#990001=LENGTH_MEASURE_WITH_UNIT(LENGTH_MEASURE(25.4),#990002);\n#990002=(LENGTH_UNIT()NAMED_UNIT(*)SI_UNIT(.MILLI.,.METRE.))");
            if (inch == step) fails.Add("STEP unit text not found");
            var rs = Parametric3D.Kernel.Export.StepImport.ReadExactSolids(inch, new List<string>());
            double vs = rs.Sum(OpVolume);
            Console.WriteLine($"STEP inch: bodies {rs.Count}, mesh volume {rs.Sum(b => Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(b))):R}");
            if (!(Math.Abs(vs - v0 * Math.Pow(25.4, 3)) <= 1e-6 * v0 * Math.Pow(25.4, 3))) fails.Add($"STEP inch volume {vs:R}");
            // IGES: наш файл — мм (признак 2, «2HMM»); тот же файл с признаком дюйма — в 25.4 раза больше
            string iges = Parametric3D.Kernel.Export.IgesSolid.Write(box);
            var ri = Parametric3D.Kernel.Export.IgesImport.Read(iges);
            double vi = ri == null ? double.NaN : AnalyticVolume.Of(ri) ?? Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(ri));
            if (!(Math.Abs(vi - v0) <= 1e-6 * v0)) fails.Add($"IGES mm volume {vi:R}");
            string igesIn = ReplaceIgesUnits(iges, "1", "2HIN");
            var w = new List<string>(); var rin = Parametric3D.Kernel.Export.IgesImport.Read(igesIn, w);
            double vin = rin == null ? double.NaN : AnalyticVolume.Of(rin) ?? Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(rin));
            Console.WriteLine($"UNITS-STEP-IGES-READ step {vs:R} iges {vi:R} igesIn {vin:R} note {w.FirstOrDefault()} nullIn {rin == null} warn {string.Join(" | ", w.Take(3))}");
            if (!(Math.Abs(vin - v0 * Math.Pow(25.4, 3)) <= 1e-6 * v0 * Math.Pow(25.4, 3))) fails.Add($"IGES inch volume {vin:R}");
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }

    /// <summary>Меняет признак и имя единиц в общем разделе IGES (поля 14 и 15), сохраняя 72 знака в строке.</summary>
    static string ReplaceIgesUnits(string iges, string flag, string name)
    {
        var lines = iges.Split('\n').ToList();
        var g = lines.Where(l => l.Length >= 73 && l[72] == 'G').ToList();
        string all = string.Concat(g.Select(l => l[..72]));
        string patched = all.Replace(",2,2HMM,", $",{flag},{name},");
        if (patched == all) throw new Exception("IGES unit fields not found");
        var chunks = new List<string>(); for (int i = 0; i < patched.Length; i += 72) chunks.Add(patched.Substring(i, Math.Min(72, patched.Length - i)).PadRight(72));
        int gi = 0; var res = new List<string>(); bool done = false;
        foreach (var l in lines)
        {
            if (l.Length >= 73 && l[72] == 'G') { if (!done) { foreach (var c in chunks) res.Add(c + "G" + (++gi).ToString().PadLeft(7)); done = true; } continue; }
            res.Add(l);
        }
        // счётчик строк общего раздела в конце файла (T) — поправить
        for (int i = 0; i < res.Count; i++)
            if (res[i].Length >= 73 && res[i][72] == 'T') res[i] = System.Text.RegularExpressions.Regex.Replace(res[i], @"G\s*\d+", m => "G" + gi.ToString().PadLeft(m.Length - 1));
        return string.Join("\n", res);
    }
}

static partial class Program
{
    /// <summary>
    /// m.f3: скругление и фаска вершины прямым построением — грань «round:vertex» (точная сфера) или «chamfer:vertex» (точная
    /// плоскость), рёбра доходят до точек касания/среза ТОЧНО (прежде шар-резец раздувался на 2e-5·R, и рёбра кончались в
    /// ~0.04 мм от касания), гарантия ExactAnalytic, объём — по формуле. Выпуклый угол бруска (скругление и фаска); прочие углы
    /// (призма, вогнутый угол кармана) — регрессия K09-VERTEX/VOPSN/VCONCAVE. Мутация P3D_DRVERTEX=0 — прежний путь резцов.
    /// </summary>
    static void VertexDirectChecks()
    {
        Check("K09-VERTEX-DIRECT-EXACT", () =>
        {
            var fails = new List<string>();
            Part Cube() { var p = new Part("v"); TestGeometry.AddCube(p, 60); p.Rebuild(null); return p; }
            var corner = new Vector3d(30, 30, 30);
            bool Has(Part p, Vector3d x) => p.Bodies[0].Vertices.Any(v => (v.P - x).Length < 1e-9);
            {
                var p = Cube(); var f = new Parametric3D.Model.Features.VertexRoundFeature { Vertex = corner, Radius = new Parametric3D.Model.Features.ValueRef(6) };
                long b0 = DirectRound.VertexBuilt;
                p.AddFeature(f); p.Rebuild(null);
                if (DirectRound.VertexBuilt == b0) fails.Add("round: the direct builder was not used");
                var b = p.Bodies[0]; double sb = 6 / Math.Sqrt(2);
                if (f.Error != null) fails.Add("round: " + f.Error);
                else
                {
                    if (b.Guarantee != Parametric3D.Kernel.GuaranteeLevel.ExactAnalytic) fails.Add("round: guarantee " + b.Guarantee);
                    foreach (var t in new[] { new Vector3d(30 - sb, 30, 30), new Vector3d(30, 30 - sb, 30), new Vector3d(30, 30, 30 - sb) })
                        if (!Has(p, t)) fails.Add($"round: tangent point {t:0.######} is not a vertex");
                    double want = 216000 - VertexRoundRemoved(6);
                    if (!(Math.Abs(p.Volume - want) <= 1e-9 * want)) fails.Add($"round: volume {p.Volume:R} vs {want:R}");
                    if (!SolidValidator.Validate(b).IsValid) fails.Add("round: invalid");
                }
            }
            {
                var p = Cube(); var f = new Parametric3D.Model.Features.VertexChamferFeature { Vertex = corner, Distance = new Parametric3D.Model.Features.ValueRef(6) };
                long b0 = DirectRound.VertexBuilt;
                p.AddFeature(f); p.Rebuild(null);
                if (DirectRound.VertexBuilt == b0) fails.Add("chamfer: the direct builder was not used");
                if (f.Error != null) fails.Add("chamfer: " + f.Error);
                else
                {
                    foreach (var t in new[] { new Vector3d(24, 30, 30), new Vector3d(30, 24, 30), new Vector3d(30, 30, 24) }) if (!Has(p, t)) fails.Add($"chamfer: {t} is not a vertex");
                    if (!(Math.Abs(p.Volume - (216000 - 36)) <= 1e-12 * 216000)) fails.Add($"chamfer: volume {p.Volume:R}");
                }
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}

static partial class Program
{
    /// <summary>
    /// KRN-02 (02.4): единые точные предикаты. Эталон знака — независимая точная арифметика здесь же (double → дробь
    /// m·2^e в BigInteger). Сетка Shewchuk у почти вырожденной прямой: точный знак совпадает с эталоном во всех точках
    /// (наивный определитель в double — нет); антисимметрия и циклическая инвариантность; точно вырожденные (коллинеарные,
    /// компланарные, коциркулярные) — 0; CDT многоугольника с почти коллинеарными точками — все треугольники одного обхода,
    /// площадь сходится. Мутация P3D_PREDICATES=0.
    /// </summary>
    static int RefO(Vector2d a, Vector2d b, Vector2d c)
    {
        static System.Numerics.BigInteger N(double x)
        {
            if (x == 0) return 0;
            long bits = BitConverter.DoubleToInt64Bits(x); int ex = (int)((bits >> 52) & 0x7FF); long mant = bits & 0xFFFFFFFFFFFFFL;
            if (ex == 0) ex = 1; else mant |= 1L << 52; ex -= 1075;
            var v = new System.Numerics.BigInteger(mant) << (ex + 1200);
            return bits < 0 ? -v : v;
        }
        return ((N(b.X) - N(a.X)) * (N(c.Y) - N(a.Y)) - (N(b.Y) - N(a.Y)) * (N(c.X) - N(a.X))).Sign;
    }

    static void PredicateChecks()
    {
        Check("KRN02-PREDICATES-EXACT-CONSISTENT", () =>
        {
            var fails = new List<string>();
            static System.Numerics.BigInteger Num(double x, int e0)
            {
                // x = mant·2^exp; приведение к общему порядку e0 (e0 ≤ exp)
                if (x == 0) return 0;
                long bits = BitConverter.DoubleToInt64Bits(x); int ex = (int)((bits >> 52) & 0x7FF); long mant = bits & 0xFFFFFFFFFFFFFL;
                if (ex == 0) ex = 1; else mant |= 1L << 52; ex -= 1075;
                var v = new System.Numerics.BigInteger(mant) << (ex - e0);
                return bits < 0 ? -v : v;
            }
            static int RefOrient(Vector2d a, Vector2d b, Vector2d c)
            {
                const int e0 = -1200;
                System.Numerics.BigInteger ax = Num(a.X, e0), ay = Num(a.Y, e0), bx = Num(b.X, e0), by = Num(b.Y, e0), cx = Num(c.X, e0), cy = Num(c.Y, e0);
                return ((bx - ax) * (cy - ay) - (by - ay) * (cx - ax)).Sign;
            }
            int naiveWrong = 0, exactWrong = 0, n = 0;
            var q = new Vector2d(12, 12); var r = new Vector2d(24, 24);
            double ulp = Math.BitIncrement(0.5) - 0.5;
            for (int i = 0; i < 128; i++)
                for (int j = 0; j < 128; j++)
                {
                    var p = new Vector2d(0.5 + i * ulp, 0.5 + j * ulp);
                    int want = RefOrient(p, q, r); n++;
                    if (Parametric3D.Kernel.Robust.Predicates.Orient2D(p, q, r) != want) exactWrong++;
                    double naive = (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X);
                    if (Math.Sign(naive) != want) naiveWrong++;
                    // антисимметрия и циклическая инвариантность
                    int s = Parametric3D.Kernel.Robust.Predicates.Orient2D(p, q, r);
                    if (Parametric3D.Kernel.Robust.Predicates.Orient2D(q, r, p) != s || Parametric3D.Kernel.Robust.Predicates.Orient2D(q, p, r) != -s) exactWrong++;
                }
            Log(new { test = "KRN02-PREDICATES-EXACT-CONSISTENT", n, naiveWrong, exactWrong });
            if (exactWrong > 0) fails.Add($"orient2d: {exactWrong} of {n} wrong or inconsistent");
            if (naiveWrong == 0) fails.Add("the near-degenerate grid did not stress the naive determinant (test not meaningful)");
            // точно вырожденные — 0
            if (Parametric3D.Kernel.Robust.Predicates.Orient2D(new(0.1, 0.1), new(0.2, 0.2), new(0.30000000000000004, 0.30000000000000004)) != 0) fails.Add("collinear (0.1, 0.2, 0.30000000000000004) not 0");
            if (Parametric3D.Kernel.Robust.Predicates.InCircle(new(5, 0), new(0, 5), new(-3, 4), new(4, -3)) != 0) fails.Add("cocircular not 0");
            if (Parametric3D.Kernel.Robust.Predicates.InCircle(new(5, 0), new(0, 5), new(-3, 4), new(4, Math.BitDecrement(-3.0))) != -1) fails.Add("just outside the circle not -1");
            if (Parametric3D.Kernel.Robust.Predicates.InCircle(new(5, 0), new(0, 5), new(-3, 4), new(4, Math.BitIncrement(-3.0))) != 1) fails.Add("just inside the circle not +1");
            if (Parametric3D.Kernel.Robust.Predicates.Orient3D(new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(0.3, 0.7, 0)) != 0) fails.Add("coplanar not 0");
            if (Parametric3D.Kernel.Robust.Predicates.Orient3D(new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(0.3, 0.7, -1e-300)) != 1) fails.Add("d just below the plane not +1");
            Require(fails.Count == 0, string.Join("; ", fails));
        });
        Check("KRN02-PREDICATES-POINT-IN-POLYGON", () =>
        {
            // ядро: GeomUtil.PointInPolygon у почти вырожденного звена должна совпадать с точным эталоном
            // (внутри ⇔ точка строго слева от восходящего звена AB; вне — справа и НА звене)
            var A = new Vector2d(-7.3, -3.1); var B = new Vector2d(17.9, 11.3); var C = new Vector2d(-50, 11.3);
            var poly = new List<Vector2d> { A, B, C };
            var P0 = A + 0.37 * (B - A);
            double ux = Math.BitIncrement(Math.Abs(P0.X)) - Math.Abs(P0.X), uy = Math.BitIncrement(Math.Abs(P0.Y)) - Math.Abs(P0.Y);
            int n = 0, wrong = 0;
            for (int i = -32; i < 32; i++)
                for (int j = -32; j < 32; j++)
                {
                    var p = new Vector2d(P0.X + i * ux, P0.Y + j * uy);
                    bool want = RefO(A, B, p) > 0; n++;
                    if (Parametric3D.Kernel.GeomUtil.PointInPolygon(poly, p) != want) wrong++;
                }
            Log(new { test = "KRN02-PREDICATES-POINT-IN-POLYGON", n, wrong });
            Require(wrong == 0, $"{wrong} of {n} points classified against the exact reference");
        });
        Check("KRN02-PREDICATES-CDT-NEAR-COLLINEAR", () =>
        {
            // многоугольник: нижняя сторона — 200 точек на прямой y = x·k с отклонениями в несколько ulp, верх — одна вершина
            var outer = new List<Vector2d>();
            var rnd = new Random(42); double k = 0.1;
            for (int i = 0; i <= 200; i++)
            {
                double x = i * 0.05, y = x * k;
                for (int t = rnd.Next(4); t > 0; t--) y = rnd.Next(2) == 0 ? Math.BitIncrement(y) : Math.BitDecrement(y);
                outer.Add(new Vector2d(x, y));
            }
            outer.Add(new Vector2d(5, 3));
            double area = 0; for (int i = 0; i < outer.Count; i++) { var a = outer[i]; var b = outer[(i + 1) % outer.Count]; area += a.X * b.Y - b.X * a.Y; } area /= 2;
            var tris = Parametric3D.Kernel.Tess.Cdt.Triangulate(outer, new List<IReadOnlyList<Vector2d>>(), out var pts);
            Require(tris != null, "triangulation failed");
            int cw = 0; double sum = 0;
            for (int i = 0; i + 2 < tris!.Count; i += 3)
            {
                int o = Parametric3D.Kernel.Robust.Predicates.Orient2D(pts[tris[i]], pts[tris[i + 1]], pts[tris[i + 2]]);
                if (o * Math.Sign(area) < 0) cw++;
                var a = pts[tris[i]]; var b = pts[tris[i + 1]]; var c = pts[tris[i + 2]];
                sum += Math.Abs((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X)) / 2;
            }
            Log(new { test = "KRN02-PREDICATES-CDT-NEAR-COLLINEAR", triangles = tris.Count / 3, cw, sum, area });
            Require(cw == 0, $"{cw} triangles wound the other way");
            Require(Math.Abs(sum - Math.Abs(area)) <= 1e-12 * Math.Abs(area), $"area {sum:R} vs {Math.Abs(area):R}");
        });
    }
}
