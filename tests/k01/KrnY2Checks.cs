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
}
