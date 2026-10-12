using OpenTK.Mathematics;

static partial class Program
{
    /// <summary>Кривая меридиана (ρ, z): отрезок a→b или окружность (центр, радиус).</summary>
    sealed record MCurve(bool IsCircle, Vector2d A, Vector2d B, double R)
    {
        public static MCurve Line(double ax, double az, double bx, double bz) => new(false, new(ax, az), new(bx, bz), 0);
        public static MCurve Circle(double cx, double cz, double r) => new(true, new(cx, cz), default, r);
        public double SDist(Vector2d p)
        {
            if (IsCircle) return (p - A).Length - R;
            var d = B - A; var n = new Vector2d(-d.Y, d.X) / d.Length; return Vector2d.Dot(p - A, n);
        }
        public Vector2d Foot(Vector2d p)
        {
            if (IsCircle) return A + (p - A).Normalized() * R;
            var d = B - A; return A + d * (Vector2d.Dot(p - A, d) / d.LengthSquared);
        }
        /// <summary>Точка пути от a к b вдоль кривой (у окружности — короткой дугой).</summary>
        public Vector2d Along(Vector2d a, Vector2d b, double s)
        {
            if (!IsCircle) return a + (b - a) * s;
            double a0 = Math.Atan2(a.Y - A.Y, a.X - A.X), b0 = Math.Atan2(b.Y - A.Y, b.X - A.X), db = b0 - a0;
            while (db > Math.PI) db -= 2 * Math.PI; while (db < -Math.PI) db += 2 * Math.PI;
            double an = a0 + db * s; return A + new Vector2d(Math.Cos(an), Math.Sin(an)) * R;
        }
    }

    /// <summary>
    /// ЭТАЛОН ДЛЯ СООСНЫХ ПОВЕРХНОСТЕЙ ВРАЩЕНИЯ (Папп): в меридиане ребро — угол P между кривыми c1 и c2, скругление —
    /// окружность радиуса r, касающаяся обеих (центр — ближайшее к P решение «расстояния до c1 и c2 равны ±r» на стороне
    /// материала: внутри многоугольника профиля у выпуклого, снаружи у вогнутого). Объём уголка — 2π∬ρ dA = 2π∮ρ²/2 dz по
    /// контуру P → касание 1 → дуга → касание 2 → P.
    /// </summary>
    static double PappusCorner(MCurve c1, MCurve c2, Vector2d P, double r, bool convex, IReadOnlyList<Vector2d> profile)
    {
        bool Inside(Vector2d p)
        {
            bool ins = false;
            for (int i = 0, j = profile.Count - 1; i < profile.Count; j = i++)
                if ((profile[i].Y > p.Y) != (profile[j].Y > p.Y) && p.X < (profile[j].X - profile[i].X) * (p.Y - profile[i].Y) / (profile[j].Y - profile[i].Y) + profile[i].X) ins = !ins;
            return ins;
        }
        Vector2d? best = null; double bestD = double.MaxValue;
        foreach (double s1 in new[] { 1.0, -1.0 })
            foreach (double s2 in new[] { 1.0, -1.0 })
                for (int k = 0; k < 16; k++)
                {
                    var x = P + new Vector2d(Math.Cos(2 * Math.PI * k / 16), Math.Sin(2 * Math.PI * k / 16)) * (1.5 * r);
                    for (int it = 0; it < 100; it++)
                    {
                        double f1 = c1.SDist(x) - s1 * r, f2 = c2.SDist(x) - s2 * r, h = 1e-7;
                        double j11 = (c1.SDist(x + new Vector2d(h, 0)) - c1.SDist(x)) / h, j12 = (c1.SDist(x + new Vector2d(0, h)) - c1.SDist(x)) / h;
                        double j21 = (c2.SDist(x + new Vector2d(h, 0)) - c2.SDist(x)) / h, j22 = (c2.SDist(x + new Vector2d(0, h)) - c2.SDist(x)) / h;
                        double det = j11 * j22 - j12 * j21; if (Math.Abs(det) < 1e-14) break;
                        var dx = new Vector2d((-f1 * j22 + f2 * j12) / det, (-f2 * j11 + f1 * j21) / det);
                        x += dx; if (dx.Length < 1e-15) break;
                    }
                    if (Math.Abs(c1.SDist(x) - s1 * r) > 1e-11 || Math.Abs(c2.SDist(x) - s2 * r) > 1e-11) continue;
                    if (Inside(x) != convex) continue;
                    double d = (x - P).Length; if (d > 4 * r) continue;
                    if (d < bestD - 1e-12) { bestD = d; best = x; }
                }
        if (best == null) return double.NaN;
        var c = best.Value; var t1 = c1.Foot(c); var t2 = c2.Foot(c);
        var pts = new List<Vector2d>();
        const int n = 20000;
        for (int i = 0; i <= n; i++) pts.Add(c1.Along(P, t1, (double)i / n));
        double a0 = Math.Atan2(t1.Y - c.Y, t1.X - c.X), b0 = Math.Atan2(t2.Y - c.Y, t2.X - c.X), db = b0 - a0;
        while (db > Math.PI) db -= 2 * Math.PI; while (db < -Math.PI) db += 2 * Math.PI;
        for (int i = 1; i <= n; i++) { double an = a0 + db * i / n; pts.Add(c + new Vector2d(Math.Cos(an), Math.Sin(an)) * r); }
        for (int i = 1; i <= n; i++) pts.Add(c2.Along(t2, P, (double)i / n));
        double I = 0;
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            double r0 = pts[i].X, r1 = pts[i + 1].X;
            I += (r0 * r0 + r0 * r1 + r1 * r1) / 6 * (pts[i + 1].Y - pts[i].Y);
        }
        return Math.Abs(2 * Math.PI * I);
    }
}
