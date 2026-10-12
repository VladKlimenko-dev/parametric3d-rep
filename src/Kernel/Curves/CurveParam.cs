using OpenTK.Mathematics;

namespace Parametric3D.Kernel.Curves;

/// <summary>
/// ТОЧНЫЙ БЛИЖАЙШИЙ ПАРАМЕТР НА КРИВОЙ.
///
/// ДОБАВЛЕНО (этап 1: точная поверхность — хозяин грани). Проекции поверхностей искали
/// параметр перебором — 48 проб и несколько делений шага: у окружности R20 это до 0.16 мм
/// ошибки, и параметр обрезался границами [0, 1]. Здесь: отрезок и дуга — формулой, без
/// обрезки; прочие кривые — перебор по 64 точкам и уточнение Ньютоном.
/// </summary>
public static class CurveParam
{
    static readonly bool MinimaOff = Environment.GetEnvironmentVariable("P3D_CURVEMINIMA") == "0";
    public static double Closest(ICurve3 c, Vector3d p)
    {
        switch (c)
        {
            case LineCurve3 line:
            {
                Vector3d d = line.B - line.A;
                double l2 = d.LengthSquared;
                return l2 < 1e-30 ? 0 : Vector3d.Dot(p - line.A, d) / l2;
            }
            case ArcCurve3 arc when Math.Abs(arc.Sweep) > 1e-15:
            {
                Vector2d q = arc.Frame.ToLocal2(p);
                double ang = Math.Atan2(q.Y, q.X);
                // Из равнозначных по 2π — угол, ближайший к промежутку дуги [0, 1].
                double best = double.NaN, bestGap = double.MaxValue;
                for (int k = -3; k <= 3; k++)
                {
                    double t = (ang + 2 * Math.PI * k - arc.A0) / arc.Sweep;
                    double gap = t < 0 ? -t : t > 1 ? t - 1 : 0;
                    if (gap < bestGap) { bestGap = gap; best = t; }
                }
                return best;
            }
        }

        // Прочие кривые: перебор и уточнение Ньютоном по g(t) = (c(t) − p)·c'(t).
        const int N = 64;
        double bt = 0, bd = double.MaxValue;
        var ds = new double[N + 1];
        for (int i = 0; i <= N; i++)
        {
            double t = i / (double)N;
            double d = ds[i] = (c.PointAt(t) - p).LengthSquared;
            if (d < bd) { bd = d; bt = t; }
        }
        // g.04: ВСЕ ЛОКАЛЬНЫЕ МИНИМУМЫ ПЕРЕБОРА уточняются делением золотым сечением в своей скобке, берётся ближайший.
        // Прежде — только лучший отсчёт и Ньютон с шагом по средней скорости параметра: у сплайна (параметр не по длине)
        // проекция на выдавливание сплайна уходила в чужой минимум — 0.17 мм при истинных 0.003 (G04-FACETING-MEASURED-
        // WITHIN-PROMISE, призма над сплайнами). Мутация P3D_CURVEMINIMA=0.
        if (!MinimaOff)
        {
            const double gr = 0.6180339887498949;
            for (int i = 0; i <= N; i++)
            {
                if (!((i == 0 || ds[i] <= ds[i - 1]) && (i == N || ds[i] <= ds[i + 1]))) continue;
                double lo = Math.Max(0, (i - 1) / (double)N), hi = Math.Min(1, (i + 1) / (double)N);
                double x1 = hi - gr * (hi - lo), x2 = lo + gr * (hi - lo);
                double f1 = (c.PointAt(x1) - p).LengthSquared, f2 = (c.PointAt(x2) - p).LengthSquared;
                for (int it = 0; it < 48 && hi - lo > 1e-13; it++)
                {
                    if (f1 < f2) { hi = x2; x2 = x1; f2 = f1; x1 = hi - gr * (hi - lo); f1 = (c.PointAt(x1) - p).LengthSquared; }
                    else { lo = x1; x1 = x2; f1 = f2; x2 = lo + gr * (hi - lo); f2 = (c.PointAt(x2) - p).LengthSquared; }
                }
                double tm = (lo + hi) / 2, dm = (c.PointAt(tm) - p).LengthSquared;
                if (dm < bd) { bd = dm; bt = tm; }
            }
        }
        // УТОЧНЕНИЕ ПО ТОЧНОЙ КАСАТЕЛЬНОЙ, С ЗАЩИТОЙ. Прежде производная бралась разностью с
        // шагом 1e-6: у процедурной кривой (пересечение поверхностей) точка вычисляется
        // итерациями, такая производная — шум, Ньютон уходил в сторону и терял найденное
        // перебором; ошибка доходила до десятых долей миллиметра. Теперь шаг считается по
        // касательной кривой, а результат принимается, только если он ближе.
        double t0 = bt;
        for (int it = 0; it < 12; it++)
        {
            Vector3d at = c.PointAt(t0);
            Vector3d tan = c.TangentAt(t0);
            double len = tan.Length;
            if (len < 1e-12) break;
            // Масштаб касательной — в единицах параметра: длина кривой на единицу t.
            double speed = c.Length > 0 ? c.Length : 1;
            double step = Vector3d.Dot(at - p, tan / len) / speed;
            double next = Math.Clamp(t0 - step, 0, 1);
            if (Math.Abs(next - t0) < 1e-12) { t0 = next; break; }
            t0 = next;
        }
        return (c.PointAt(t0) - p).LengthSquared <= bd ? t0 : bt;
    }
}
