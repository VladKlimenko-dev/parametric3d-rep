using OpenTK.Mathematics;
using Parametric3D.Kernel.Surfaces;

static partial class Program
{
    /// <summary>
    /// Я2: ТАБЛИЦА ПРОЦЕДУРНОЙ ЛЕНТЫ ГЛАДКАЯ (C2 по u). Скользящее окно Лагранжа давало поверхность лишь C0: в узле таблицы
    /// касательная прыгала, и основание перпендикуляра у точки в узле не находилось (s102: общий параметр ребра — NaN, скругление
    /// у прежней фаски уходило в резец). Лента — дуги переменного радиуса вдоль винтовой линии, таблица крупная (узлов мало,
    /// ошибка вставки велика — излом заметен). Норма: у точки самой поверхности в каждом внутреннем узле основание найдено и
    /// совпадает с точкой; излом касательной в узлах — на уровне шума разностей. Мутация P3D_SECSMOOTH=0 — проверка падает.
    /// </summary>
    static void KrnY2SectionTableChecks()
    {
        Check("KRN-Y2-SECTION-TABLE-C2", () =>
        {
            SweepSection Gen(double u)
            {
                var c = new Vector3d(10 * Math.Cos(0.15 * u), 10 * Math.Sin(0.15 * u), 0.8 * u);
                var e1 = new Vector3d(Math.Cos(0.15 * u), Math.Sin(0.15 * u), 0);
                var e2 = Vector3d.UnitZ;
                return SweepSection.Arc(c, e1, e2, 2 + 0.8 * Math.Sin(0.4 * u), 1.2 + 0.2 * Math.Cos(0.3 * u));
            }
            double u0 = 0, u1 = 20;
            var us = Enumerable.Range(0, 9).Select(i => u0 + (u1 - u0) * i / 8.0).ToList();
            // шаг таблицы 0.3 (с множителем вставки — 2.4): 9 узлов на промежутке 20
            var ss = new SectionSurface(Gen, us, us.Select(Gen).ToList(), false, u0, u1, null, 0.3);
            int fails = 0, nodes = 0; double worstKink = 0, worstFoot = 0;
            double h = (u1 - u0) / Math.Ceiling((u1 - u0) / 2.4);
            for (double un = u0 + h; un < u1 - h / 2; un += h)
            {
                nodes++;
                foreach (double v in new[] { 0.3, 0.5, 0.8 })
                {
                    var x = ss.PointAt(new Vector2d(un, v));
                    var (p, _) = Parametric3D.Kernel.Geometry.Ssi.ParamFoot(ss, x);
                    if (!double.IsFinite(p.X) || (p - x).Length > 1e-9) fails++;
                    else worstFoot = Math.Max(worstFoot, (p - x).Length);
                    const double d = 1e-4;
                    var a = ss.PointAt(new Vector2d(un - d, v)); var b = ss.PointAt(new Vector2d(un + d, v));
                    double kink = ((b - x) / d - (x - a) / d).Length;          // скачок касательной + O(d·S_uu)
                    var a2 = ss.PointAt(new Vector2d(un - 2 * d, v)); var b2 = ss.PointAt(new Vector2d(un + 2 * d, v));
                    double kink2 = ((b2 - x) / (2 * d) - (x - a2) / (2 * d)).Length;
                    // гладкая поверхность: разность растёт вдвое с шагом (O(d)); излом — не зависит от шага
                    worstKink = Math.Max(worstKink, Math.Abs(2 * kink - kink2));
                }
            }
            Log(new { test = "KRN-Y2-SECTION-TABLE-C2", nodes, fails, worstKink, worstFoot });
            Require(nodes >= 5, $"only {nodes} interior nodes");
            Require(fails == 0, $"{fails} foot point(s) not found at table nodes");
            Require(worstKink < 1e-6, $"tangent jump at a table node {worstKink:E2}");
        });
    }
    /// <summary>
    /// Я2: СТЫК КУСКОВ ТАБЛИЦЫ НЕПРЕРЫВЕН. Лента по цепочке звеньев (формула со стыком: хребет — ломаная с изломом скорости)
    /// таблицей по кускам; узел у стыка считался в 1e-9 изнутри куска, и куски расходились на S_u·2e-9 — у вершины на стыке
    /// основание не находилось (s102 шаг 1). Норма: разрыв в стыке меньше 1e-12 мм, основание точки стыка найдено.
    /// Опыт: исправление включается P3D_SECBREAKNODE=1 (по умолчанию выключено — регрессия на s102 шаг 0), режим y2break.
    /// </summary>
    static void KrnY2SectionBreakChecks()
    {
        Check("KRN-Y2-SECTION-BREAK-C0", () =>
        {
            const double ub = 7.3;
            SweepSection Gen(double u)
            {
                // хребет: отрезок до стыка, за ним — дуга той же касательной (стык G1, как у касательной цепочки рёбер)
                Vector3d c; Vector3d t;
                if (u < ub) { c = new Vector3d(u, 0, 0); t = Vector3d.UnitX; }
                else { double a = (u - ub) / 12.0; c = new Vector3d(ub + 12 * Math.Sin(a), 12 * (1 - Math.Cos(a)), 0); t = new Vector3d(Math.Cos(a), Math.Sin(a), 0); }
                var n = Vector3d.Cross(Vector3d.UnitZ, t);
                return SweepSection.Line(c + n * 1.5, c + Vector3d.UnitZ * 1.5);
            }
            double u0 = 0, u1 = 20;
            var us = Enumerable.Range(0, 11).Select(i => u0 + (u1 - u0) * i / 10.0).ToList();
            var ss = new SectionSurface(Gen, us, us.Select(Gen).ToList(), false, u0, u1, new[] { ub }, 0.05);
            double jump = 0; int fails = 0;
            foreach (double v in new[] { 0.0, 0.3, 0.5, 1.0 })
            {
                var l = ss.PointAt(new Vector2d(ub - 1e-13, v)); var r = ss.PointAt(new Vector2d(ub + 1e-13, v));
                jump = Math.Max(jump, (l - r).Length);
                var x = Gen(ub).Point(v);
                var (p, _) = Parametric3D.Kernel.Geometry.Ssi.ParamFoot(ss, x);
                if (!double.IsFinite(p.X) || (p - x).Length > 1e-9) fails++;
            }
            Log(new { test = "KRN-Y2-SECTION-BREAK-C0", jump, fails });
            Require(jump < 1e-12, $"table pieces part at the joint by {jump:E2} mm");
            Require(fails == 0, $"{fails} foot point(s) not found at the joint");
        });
    }
    /// <summary>
    /// Я2: ОСНОВАНИЕ ПЕРПЕНДИКУЛЯРА НА СКЛАДКЕ ЛЕНТЫ. Лента — стенка вдоль ломаной с поворотом 30° в стыке кусков (складка G0,
    /// как стык прежней фаски по цепочке). Основание (Ssi.ParamFoot) точки на линии стыка не находилось: ортогонального
    /// основания у складки нет, Ньютон качался через неё на 1e-9 мм (s110 шаг 2: формула новой фаски не определена там, где
    /// касание на стыке прежней). Норма: основание точек складки найдено, отход меньше 1e-8 мм. Мутация P3D_FOOTONSURF=0 —
    /// проверка падает.
    /// </summary>
    static void KrnY2SectionCreaseFootChecks()
    {
        Check("KRN-Y2-SECTION-CREASE-FOOT", () =>
        {
            const double ub = 6.1;
            double ang = 30 * Math.PI / 180;
            var t2 = new Vector3d(Math.Cos(ang), Math.Sin(ang), 0);
            SweepSection Gen(double u)
            {
                Vector3d c = u < ub ? new Vector3d(u, 0, 0) : new Vector3d(ub, 0, 0) + t2 * (u - ub);
                return SweepSection.Line(c, c + new Vector3d(0, 0, 2));
            }
            double u0 = 0, u1 = 14;
            var us = Enumerable.Range(0, 15).Select(i => u0 + (u1 - u0) * i / 14.0).ToList();
            var ss = new SectionSurface(Gen, us, us.Select(Gen).ToList(), false, u0, u1, new[] { ub }, 0.05);
            int fails = 0; double worst = 0;
            foreach (double v in new[] { 0.2, 0.5, 0.8 })
            {
                var x = Gen(ub).Point(v);
                var (p, _) = Parametric3D.Kernel.Geometry.Ssi.ParamFoot(ss, x);
                if (!double.IsFinite(p.X)) fails++; else worst = Math.Max(worst, (p - x).Length);
            }
            Log(new { test = "KRN-Y2-SECTION-CREASE-FOOT", fails, worst });
            Require(fails == 0, $"{fails} foot point(s) on the crease not found");
            Require(worst < 1e-8, $"foot on the crease is off by {worst:E2} mm");
        });
    }
    /// <summary>
    /// Я2: ФАСКА ПО ЦЕПОЧКЕ ЧЕРЕЗ СТЫК ЛЕНТЫ ПРЕЖНЕЙ ФАСКИ — ПРЯМО И ТОЧНО (корпус s110 шаг 2: C6 по цепочке из 4 рёбер, грань B —
    /// лента прежней фаски со стыком кусков). Отход таблицы новой ленты от формулы был 2.2e-4 мм (допуск 2e-8) — отказ, резец,
    /// многогранный итог. Теперь таблица перестраивается мельче, пока отход велик. Норма: шаг 2 — ExactAnalytic.
    /// Мутация P3D_DRTABREFINE=0 — проверка падает.
    /// </summary>
    static void KrnY2ChainChamferChecks()
    {
        Check("KRN-Y2-S110-CHAIN-CHAMFER-EXACT", () =>
        {
            LoftCorpusSeq(1, 110, 3, Path.Combine(Path.GetTempPath(), "p3d_y2_s110"));
            List<(string Tag, string Guarantee)> st; lock (LastSeqSteps) st = LastSeqSteps.ToList();
            var s2 = st.FirstOrDefault(x => x.Tag.StartsWith("s110 step 2"));
            Log(new { test = "KRN-Y2-S110-CHAIN-CHAMFER-EXACT", steps = st.Select(x => $"{x.Tag[..Math.Min(40, x.Tag.Length)]}: {x.Guarantee}").ToList() });
            Require(s2.Tag != null, "step 2 not built");
            Require(s2.Guarantee == "ExactAnalytic", $"step 2 is {s2.Guarantee}");
        });
    }
    /// <summary>
    /// Я2: ПЕРЕПОЛНЕНИЕ ЛЕНТЫ НА СЛЕДУЮЩУЮ ГРАНЬ У КОНЦА — ПРЯМО И ТОЧНО (корпус s102 шаг 2: скругление R2.09; ребро торца у
    /// конца 1 короче ширины ленты, линия касания уходит за его дальний конец на грань прежней фаски). Прежде — отказ «another
    /// edge of the face in the blend strip», резец, многогранный итог. Норма: шаг 2 — ExactAnalytic.
    /// Мутации P3D_DRENDOVER=0 (переполнения нет), P3D_SLABVCLAMP=0 (объём грани прежней фаски у вершины веера, s102),
    /// P3D_DRTWOSWITCH=0 (переполнение с переходом шва на другом конце, s105) — падает.
    /// </summary>
    static void KrnY2EndOverflowChecks()
    {
        Check("KRN-Y2-S102-END-OVERFLOW-EXACT", () =>
        {
            // s102 шаг 2 — переполнение у конца 1; s105 шаг 2 (фаска 4.69 по цепочке) — у конца 0, на конце 1 — переход шва
            foreach (int seed in new[] { 102, 105 })
            {
                LoftCorpusSeq(1, seed, 3, Path.Combine(Path.GetTempPath(), $"p3d_y2_s{seed}"));
                List<(string Tag, string Guarantee)> st; lock (LastSeqSteps) st = LastSeqSteps.ToList();
                var s2 = st.FirstOrDefault(x => x.Tag.StartsWith($"s{seed} step 2"));
                Log(new { test = "KRN-Y2-S102-END-OVERFLOW-EXACT", seed, steps = st.Select(x => $"{x.Tag[..Math.Min(40, x.Tag.Length)]}: {x.Guarantee}").ToList(), overflows = Parametric3D.Kernel.Ops.DirectRound.EndOvers });
                Require(s2.Tag != null, $"s{seed} step 2 not built");
                Require(s2.Guarantee == "ExactAnalytic", $"s{seed} step 2 is {s2.Guarantee}");
            }
        });
    }
    /// <summary>
    /// Я2: ОСНОВАНИЕ У ТОЧКИ СНАРУЖИ ВЫПУКЛОЙ СКЛАДКИ ЛЕНТЫ — НА САМОЙ СКЛАДКЕ. Стенка вдоль ломаной с поворотом 30° (как стык
    /// прежней фаски по цепочке); точка в 0.5 мм снаружи угла по биссектрисе. Ортогонального основания нет ни на одном куске
    /// (прежде NaN — шар катящегося скругления через складку не находился, s109 шаг 5). Норма: основание — точка складки,
    /// расстояние 0.5, нормаль — к точке. Мутация P3D_FOOTCREASE=0 — проверка падает.
    /// </summary>
    static void KrnY2CreaseOffsetFootChecks()
    {
        Check("KRN-Y2-SECTION-CREASE-OFFSET-FOOT", () =>
        {
            const double ub = 6.1;
            double ang = 30 * Math.PI / 180;
            var t2 = new Vector3d(Math.Cos(ang), Math.Sin(ang), 0);
            SweepSection Gen(double u)
            {
                Vector3d c = u < ub ? new Vector3d(u, 0, 0) : new Vector3d(ub, 0, 0) + t2 * (u - ub);
                return SweepSection.Line(c, c + new Vector3d(0, 0, 2));
            }
            double u0 = 0, u1 = 14;
            var us = Enumerable.Range(0, 15).Select(i => u0 + (u1 - u0) * i / 14.0).ToList();
            var ss = new SectionSurface(Gen, us, us.Select(Gen).ToList(), false, u0, u1, new[] { ub }, 0.05);
            // наружу угла: нормали кусков n1 = (0, -1, 0)·s, n2 = (sin, -cos, 0)·s; биссектриса — по их сумме (сторона — выпуклая)
            var n1 = new Vector3d(0, -1, 0); var n2 = new Vector3d(Math.Sin(ang), -Math.Cos(ang), 0);
            var bis = (n1 + n2).Normalized();
            int fails = 0; double worst = 0;
            foreach (double z in new[] { 0.4, 1.0, 1.6 })
                foreach (double sg in new[] { 1.0, -1.0 })
                {
                    var c = new Vector3d(ub, 0, z); var x = c + bis * (0.5 * sg);
                    var (p, nn) = Parametric3D.Kernel.Geometry.Ssi.ParamFoot(ss, x);
                    if (!double.IsFinite(p.X)) { fails++; continue; }
                    // снаружи выпуклой стороны — точка складки; с вогнутой стороны основание обычное (на куске), отход любой
                    if (Vector3d.Dot(x - c, ss.NormalAt(new Vector2d(ub - 0.5, 0.5))) * Vector3d.Dot(bis, ss.NormalAt(new Vector2d(ub - 0.5, 0.5))) > 0)
                        worst = Math.Max(worst, (p - c).Length);
                }
            Log(new { test = "KRN-Y2-SECTION-CREASE-OFFSET-FOOT", fails, worst });
            Require(fails == 0, $"{fails} foot point(s) not found");
            Require(worst < 1e-7, $"foot outside the convex crease is off the crease by {worst:E2} mm");
        });
    }
}
