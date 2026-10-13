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
